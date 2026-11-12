#!/usr/bin/env python3
"""Corpus sampling for RetryScope (docs/corpus-protocol.md). Standard library only; needs an authenticated `gh` CLI.

    python tools/sample_corpus.py frame          # query GitHub, write corpus/frame/*.json (run once, then commit)
    python tools/sample_corpus.py draw           # seeded order per stratum, corpus/frame/<stratum>-order.txt
    python tools/sample_corpus.py next           # pin the next round (5 per stratum) into corpus/sample.csv

The frame and order files are committed before any repository is opened. Exclusions after drawing are recorded by
editing the ledger's status/reason columns (see the protocol, section 4), then `next` draws replacements.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import random
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FRAME = ROOT / "corpus" / "frame"
LEDGER = ROOT / "corpus" / "sample.csv"

# Rarest first: a repository found by several queries belongs to the first stratum that finds it.
STRATA = {
    "S3": ["AddResilienceHandler"],
    "S4": ["AddTransientHttpErrorPolicy", "AddPolicyHandler"],
    "S2": ["AddStandardResilienceHandler AddHttpClient"],
    "S1": ["ConfigureHttpClientDefaults AddStandardResilienceHandler"],
}
SIZE_RANGES = ["0..1500", "1501..3000", "3001..6000", ">6000"]
EXCLUDED = {"dotnet/eshop", "dotnet/aspire-samples", "osamah3/retryscope"}
PER_ROUND = 5
LEDGER_FIELDS = ["round", "stratum", "rank", "repo", "commit", "drawn_utc", "status", "reason"]


def gh_json(*args: str) -> dict:
    """Runs `gh api ...` and returns parsed JSON, waiting out secondary rate limits."""
    for attempt in range(6):
        result = subprocess.run(["gh", "api", *args], capture_output=True, text=True, encoding="utf-8")
        if result.returncode == 0:
            return json.loads(result.stdout)
        message = result.stderr + result.stdout
        if "rate limit" in message.lower():  # primary or secondary rate limit: wait and retry
            time.sleep(60 * (attempt + 1))
            continue
        raise RuntimeError(f"gh api {' '.join(args)} failed: {message.strip()}")
    raise RuntimeError("rate limit did not clear")


def search(query: str, max_pages: int) -> tuple[int, list[dict]]:
    """All pages (up to the API's 1000-result cap) for one code-search query."""
    items: list[dict] = []
    total = 0
    for page in range(1, max_pages + 1):
        try:
            data = gh_json("-X", "GET", "search/code", "-f", f"q={query}", "-f", "per_page=100", "-f", f"page={page}")
        except RuntimeError as error:
            if "HTTP 422" in str(error):  # beyond the 1000-result window
                break
            raise
        total = data.get("total_count", 0)
        items.extend(data.get("items", []))
        time.sleep(7)  # code search allows 10 requests per minute
        if len(data.get("items", [])) < 100:
            break
    return total, items


def cmd_frame(args: argparse.Namespace) -> None:
    FRAME.mkdir(parents=True, exist_ok=True)
    if any(FRAME.glob("S*.json")) and not args.force:
        sys.exit("Frame already exists; it is retrieved once. Use --force only before anything has been drawn.")

    seen: set[str] = set()
    meta = {"retrieved_utc": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"), "queries": []}
    for stratum, terms in STRATA.items():
        repos: dict[str, dict] = {}
        for term in terms:
            for size in SIZE_RANGES:
                query = f"{term} language:C# size:{size}"
                total, items = search(query, args.max_pages)
                meta["queries"].append({"stratum": stratum, "query": query, "total_count": total, "retrieved": len(items)})
                print(f"{stratum} {query!r}: {len(items)} of {total}", flush=True)
                for item in items:
                    repo = item["repository"]
                    name = repo["full_name"]
                    if repo.get("fork") or name.lower() in EXCLUDED or name.lower() in seen:
                        continue
                    entry = repos.setdefault(name, {"repo": name, "files": []})
                    entry["files"].append(item["path"])
        seen.update(name.lower() for name in repos)
        out = sorted(repos.values(), key=lambda r: r["repo"].lower())
        (FRAME / f"{stratum}.json").write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
        print(f"{stratum}: {len(out)} repositories", flush=True)
    (FRAME / "frame-meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")


def cmd_draw(_: argparse.Namespace) -> None:
    for stratum in STRATA:
        repos = [r["repo"] for r in json.loads((FRAME / f"{stratum}.json").read_text(encoding="utf-8"))]
        repos.sort(key=str.lower)
        random.Random(f"retryscope-corpus-v1-{stratum}").shuffle(repos)
        (FRAME / f"{stratum}-order.txt").write_text("\n".join(repos) + "\n", encoding="utf-8")
        print(f"{stratum}: {len(repos)} repositories ordered")


def read_ledger() -> list[dict]:
    if not LEDGER.exists():
        return []
    with LEDGER.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def cmd_next(_: argparse.Namespace) -> None:
    ledger = read_ledger()
    round_number = max((int(r["round"]) for r in ledger), default=0) + 1
    drawn = {r["repo"].lower() for r in ledger}
    active = {}  # stratum -> repositories still counting (not replaced)
    for row in ledger:
        if not row["status"].startswith("replaced"):
            active.setdefault(row["stratum"], 0)
            active[row["stratum"]] += 1

    new_rows = []
    for stratum in STRATA:
        order = (FRAME / f"{stratum}-order.txt").read_text(encoding="utf-8").split()
        # Top up: replacements first, so each stratum has PER_ROUND * round_number active repositories.
        needed = PER_ROUND * round_number - active.get(stratum, 0)
        for rank, repo in enumerate(order, start=1):
            if needed <= 0:
                break
            if repo.lower() in drawn:
                continue
            status, reason, commit = "drawn", "", ""
            try:
                info = gh_json(f"repos/{repo}")
                if info.get("fork"):
                    status, reason = "replaced-fork", "fork"
                else:
                    commit = gh_json(f"repos/{repo}/commits/{info['default_branch']}")["sha"]
            except RuntimeError as error:
                # Protocol section 4.2: deleted, private or blocked since the frame was retrieved.
                status, reason = "replaced-missing", str(error).splitlines()[-1][:120]
            new_rows.append({
                "round": round_number, "stratum": stratum, "rank": rank, "repo": repo, "commit": commit,
                "drawn_utc": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
                "status": status, "reason": reason,
            })
            drawn.add(repo.lower())
            if status == "drawn":
                needed -= 1

    LEDGER.parent.mkdir(parents=True, exist_ok=True)
    write_header = not LEDGER.exists()
    with LEDGER.open("a", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=LEDGER_FIELDS)
        if write_header:
            writer.writeheader()
        writer.writerows(new_rows)
    for row in new_rows:
        print(f"round {row['round']} {row['stratum']} #{row['rank']}: {row['repo']} {row['commit'][:7]} {row['status']}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    frame = sub.add_parser("frame", help="retrieve the sampling frame (once)")
    frame.add_argument("--max-pages", type=int, default=10, help="pages of 100 per query (API cap: 10)")
    frame.add_argument("--force", action="store_true")
    sub.add_parser("draw", help="write the seeded order per stratum")
    sub.add_parser("next", help="pin the next round into the ledger")
    args = parser.parse_args()
    {"frame": cmd_frame, "draw": cmd_draw, "next": cmd_next}[args.command](args)


if __name__ == "__main__":
    main()
