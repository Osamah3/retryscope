#!/usr/bin/env python3
"""Label files for the evaluation corpus (docs/corpus-protocol.md section 5; docs/labelling-workflow.md).

    python tools/label_sheet.py new owner/repo          # create corpus/labels/owner__repo.json from corpus/scope.csv
    python tools/label_sheet.py check                   # validate every label file
    python tools/label_sheet.py done owner/repo 35      # mark finished, with minutes spent
    python tools/label_sheet.py status                  # round-1 progress and binding count

Labelling is blind: this script reads only the ledger, the scope file and label files. It never runs the extractor.
"""
from __future__ import annotations

import csv
import datetime as dt
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LEDGER = ROOT / "corpus" / "sample.csv"
SCOPE = ROOT / "corpus" / "scope.csv"
LABELS = ROOT / "corpus" / "labels"
GUIDE_VERSION = "1.2"

CLIENT_KINDS = {"named", "typed", "default", "direct"}
SOURCES = {"none", "explicit", "config", "default", "unresolved", "opaque"}
PATTERNS = {f"P{i}" for i in range(1, 15)}
BINDING_KEYS = ["client", "clientKind", "target", "patternCodes", "attemptBoundGet", "attemptBoundPost",
                "retryCountSource", "attemptTimeoutSeconds", "totalTimeoutSeconds", "breaker", "retryHandlers",
                "conditional", "confidence", "notes"]


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def path_for(repo: str) -> Path:
    return LABELS / f"{repo.replace('/', '__')}.json"


def read_csv(path: Path) -> list[dict]:
    with path.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def round_one() -> list[dict]:
    """Protocol clarification C6: round 1 = the first 5 non-replaced repositories of each stratum, by rank."""
    ledger = read_csv(LEDGER)
    chosen = []
    for stratum in ("S3", "S4", "S2", "S1"):
        rows = sorted((r for r in ledger if r["stratum"] == stratum and r["status"] == "included"), key=lambda r: int(r["rank"]))
        chosen.extend(rows[:5])
    return chosen


def cmd_new(repo: str) -> None:
    target = path_for(repo)
    if target.exists():
        sys.exit(f"{target} already exists; edit it instead.")
    ledger = {r["repo"].lower(): r for r in read_csv(LEDGER)}
    row = ledger.get(repo.lower()) or sys.exit(f"{repo} is not in the ledger.")
    if row["status"] != "included":
        sys.exit(f"{repo} has status '{row['status']}', not 'included'.")
    projects = [r["project"] for r in read_csv(SCOPE)
                if r["repo"].lower() == repo.lower() and r["classification"] == "in-scope" and r["build"] == "ok"]
    LABELS.mkdir(parents=True, exist_ok=True)
    sheet = {
        "repo": row["repo"], "commit": row["commit"], "stratum": row["stratum"], "rank": int(row["rank"]),
        "guideVersion": GUIDE_VERSION, "pass": 1, "startedUtc": now(), "finishedUtc": None, "minutes": None,
        "projects": [{"project": p, "service": Path(p).stem, "inScope": True, "bindings": [], "notes": ""} for p in projects],
        "guideAmbiguities": [],
    }
    target.write_text(json.dumps(sheet, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Created {target.relative_to(ROOT)} with {len(projects)} project(s). Clone: D:\\corpus-work\\{repo.replace('/', '__')}")


def problems(sheet: dict) -> list[str]:
    found = []
    for project in sheet.get("projects", []):
        where = project.get("project", "?")
        if not project.get("inScope", True) and not project.get("notes"):
            found.append(f"{where}: inScope false needs a note (misclassification, clarification C3)")
        for i, b in enumerate(project.get("bindings", []), start=1):
            at = f"{where} binding {i} ({b.get('client', '?')})"
            missing = [k for k in BINDING_KEYS if k not in b]
            if missing:
                found.append(f"{at}: missing {', '.join(missing)}")
                continue
            if b["clientKind"] not in CLIENT_KINDS:
                found.append(f"{at}: clientKind must be one of {sorted(CLIENT_KINDS)}")
            if b["retryCountSource"] not in SOURCES:
                found.append(f"{at}: retryCountSource must be one of {sorted(SOURCES)}")
            if not set(b["patternCodes"]) <= PATTERNS:
                found.append(f"{at}: patternCodes must be from P1-P14")
            for key in ("attemptBoundGet", "attemptBoundPost"):
                if not (b[key] == "?" or (isinstance(b[key], int) and b[key] >= 1)):
                    found.append(f"{at}: {key} must be an integer >= 1 or \"?\"")
            for key in ("attemptTimeoutSeconds", "totalTimeoutSeconds"):
                if not (b[key] is None or b[key] == "?" or isinstance(b[key], (int, float))):
                    found.append(f"{at}: {key} must be a number, null (none) or \"?\"")
            if b["breaker"] not in (True, False, "?"):
                found.append(f"{at}: breaker must be true, false or \"?\"")
            if not isinstance(b["retryHandlers"], int) or b["retryHandlers"] < 0:
                found.append(f"{at}: retryHandlers must be an integer >= 0")
            if not isinstance(b["conditional"], bool):
                found.append(f"{at}: conditional must be true or false")
            if b["confidence"] not in (1, 2, 3):
                found.append(f"{at}: confidence must be 1, 2 or 3")
            elif b["confidence"] < 3 and not b["notes"]:
                found.append(f"{at}: notes are required when confidence < 3 (guide §3)")
    return found


def cmd_check() -> None:
    files = sorted(LABELS.glob("*.json")) if LABELS.exists() else []
    total = 0
    for file in files:
        sheet = json.loads(file.read_text(encoding="utf-8"))
        issues = problems(sheet)
        state = "done" if sheet.get("finishedUtc") else "in progress"
        print(f"{'OK ' if not issues else 'FIX'} {file.name} ({state})")
        for issue in issues:
            print(f"    - {issue}")
        total += len(issues)
    if total:
        sys.exit(1)


def cmd_done(repo: str, minutes: str) -> None:
    target = path_for(repo)
    sheet = json.loads(target.read_text(encoding="utf-8"))
    if issues := problems(sheet):
        sys.exit("Fix these first:\n  - " + "\n  - ".join(issues))
    sheet["finishedUtc"], sheet["minutes"] = now(), int(minutes)
    target.write_text(json.dumps(sheet, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{repo}: done in {minutes} min")


def cmd_status() -> None:
    bindings = done = 0
    for row in round_one():
        file = path_for(row["repo"])
        sheet = json.loads(file.read_text(encoding="utf-8")) if file.exists() else None
        count = sum(len(p["bindings"]) for p in sheet["projects"]) if sheet else 0
        state = "done" if sheet and sheet.get("finishedUtc") else "started" if sheet else "-"
        done += state == "done"
        bindings += count if state == "done" else 0
        print(f"{row['stratum']} #{row['rank']:>2} {row['repo']:<55} {state:<8} {count} binding(s)")
    print(f"\nRound 1: {done}/20 repositories done, {bindings} labelled binding(s) (stopping rule: 150)")


def main() -> None:
    args = sys.argv[1:]
    commands = {("new", 1): lambda: cmd_new(args[1]), ("check", 0): cmd_check,
                ("done", 2): lambda: cmd_done(args[1], args[2]), ("status", 0): cmd_status}
    action = commands.get((args[0], len(args) - 1)) if args else None
    if action is None:
        sys.exit(__doc__)
    action()


if __name__ == "__main__":
    main()
