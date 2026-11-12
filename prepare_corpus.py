#!/usr/bin/env python3
"""Build check and project scoping for drawn corpus repositories (docs/corpus-protocol.md, sections 1 and 4).

    python tools/prepare_corpus.py --work C:\\corpus-work

For every ledger row with status "drawn": clone at the pinned commit, list in-scope projects, order them with a seeded
shuffle, and build them in that order until 5 build. Results go to corpus/scope.csv; the ledger status becomes
"included", "replaced-build", "replaced-no-scope" or "replaced-missing". Then run `sample_corpus.py next` to draw
replacements, and this script again.

Builds run untrusted code (MSBuild targets, package scripts, source generators), so each one runs in the official .NET
SDK container with only the clone mounted. --no-docker builds on the host instead; do not use it for corpus repositories.

This script never runs the extractor and never prints source code: labelling stays blind (protocol section 5).
"""
from __future__ import annotations

import argparse
import csv
import json
import random
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LEDGER = ROOT / "corpus" / "sample.csv"
SCOPE = ROOT / "corpus" / "scope.csv"
MAX_PROJECTS = 5
BUILD_TIMEOUT_S = 15 * 60
SCOPE_FIELDS = ["repo", "commit", "project", "order", "classification", "build", "seconds", "sdk_image", "note"]

TEST_MARKERS = re.compile(r"<IsTestProject>\s*true|Microsoft\.NET\.Test\.Sdk|\"xunit|\"NUnit|MSTest\.(TestFramework|Sdk)|TUnit", re.I)
APPHOST_MARKERS = re.compile(r"Aspire\.AppHost\.Sdk|<IsAspireHost>\s*true", re.I)
CLIENT_MARKERS = re.compile(r"<UseMaui>\s*true|<UseWPF>\s*true|<UseWindowsForms>\s*true|net\d+\.\d+-(android|ios|maccatalyst|windows)", re.I)
DEPLOYABLE_MARKERS = re.compile(r"Microsoft\.NET\.Sdk\.(Web|Worker|BlazorWebAssembly|Functions)\b|<OutputType>\s*Exe\s*<|<AzureFunctionsVersion>", re.I)


def classify(path: Path) -> str:
    """in-scope, or the reason a project is out of scope (protocol section 1)."""
    text = path.read_text(encoding="utf-8", errors="replace")
    stem = path.stem
    if TEST_MARKERS.search(text) or re.search(r"(\.|^)Tests?$", stem, re.I) or stem.lower().endswith("tests"):
        return "out:test"
    if APPHOST_MARKERS.search(text):
        return "out:apphost"
    if CLIENT_MARKERS.search(text):
        return "out:mobile-desktop"
    if not DEPLOYABLE_MARKERS.search(text):
        return "out:library"
    return "in-scope"


def sdk_image(clone: Path) -> str:
    """SDK container matching the repository's global.json major version (default 10.0)."""
    global_json = clone / "global.json"
    try:
        version = json.loads(global_json.read_text(encoding="utf-8-sig"))["sdk"]["version"]
        major = int(version.split(".")[0])
        return f"mcr.microsoft.com/dotnet/sdk:{major}.0"
    except (OSError, KeyError, ValueError, TypeError, json.JSONDecodeError):
        return "mcr.microsoft.com/dotnet/sdk:10.0"


def run(command: list[str], timeout: int) -> tuple[int | None, str]:
    try:
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return result.returncode, (result.stdout + result.stderr)
    except subprocess.TimeoutExpired:
        return None, "timeout"


def build(clone: Path, project: str, image: str, use_docker: bool) -> tuple[str, float, str]:
    started = time.monotonic()
    if use_docker:
        command = ["docker", "run", "--rm",
                   "-v", f"{clone}:/src", "-w", "/src",
                   "-v", "retryscope-nuget:/root/.nuget/packages",
                   "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
                   image, "dotnet", "build", project, "-nologo", "-v", "quiet"]
    else:
        command = ["dotnet", "build", str(clone / project), "-nologo", "-v", "quiet"]
    code, output = run(command, BUILD_TIMEOUT_S)
    seconds = round(time.monotonic() - started, 1)
    if code == 0:
        return "ok", seconds, ""
    if code is None:
        return "timeout", seconds, f"exceeded {BUILD_TIMEOUT_S} s"
    errors = [line.strip() for line in output.splitlines() if " error " in line or line.startswith("error")]
    return "failed", seconds, (errors[0] if errors else output.strip().splitlines()[-1] if output.strip() else "failed")[:200]


def prepare(row: dict, work: Path, use_docker: bool) -> list[dict]:
    repo, commit = row["repo"], row["commit"]
    clone = work / repo.replace("/", "__")
    if not clone.exists():
        code, output = run(["git", "clone", "--quiet", f"https://github.com/{repo}.git", str(clone)], 1800)
        if code != 0:
            row["status"], row["reason"] = "replaced-missing", f"clone failed: {output.strip()[-120:]}"
            return []
    code, output = run(["git", "-C", str(clone), "checkout", "--quiet", commit], 300)
    if code != 0:
        row["status"], row["reason"] = "replaced-missing", f"checkout failed: {output.strip()[-120:]}"
        return []

    projects = sorted(
        (p for p in clone.rglob("*.csproj") if not {"bin", "obj"} & set(p.relative_to(clone).parts)),
        key=lambda p: p.relative_to(clone).as_posix().lower())
    classified = [(p.relative_to(clone).as_posix(), classify(p)) for p in projects]
    in_scope = [path for path, kind in classified if kind == "in-scope"]
    random.Random(f"retryscope-projects-v1-{repo}").shuffle(in_scope)

    image = sdk_image(clone)
    rows = [{"repo": repo, "commit": commit, "project": path, "order": "", "classification": kind,
             "build": "", "seconds": "", "sdk_image": "", "note": ""}
            for path, kind in classified if kind != "in-scope"]

    built = 0
    for order, path in enumerate(in_scope, start=1):
        entry = {"repo": repo, "commit": commit, "project": path, "order": order, "classification": "in-scope",
                 "build": "not-needed", "seconds": "", "sdk_image": image, "note": ""}
        if built < MAX_PROJECTS:
            print(f"  building {path} ...", flush=True)
            entry["build"], entry["seconds"], entry["note"] = build(clone, path, image, use_docker)
            built += entry["build"] == "ok"
        rows.append(entry)

    if not in_scope:
        row["status"], row["reason"] = "replaced-no-scope", "no deployable non-test project"
    elif built == 0:
        row["status"], row["reason"] = "replaced-build", "no in-scope project builds"
    else:
        row["status"], row["reason"] = "included", f"{built} project(s) built"
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--work", required=True, type=Path, help="folder for clones (outside this repository)")
    parser.add_argument("--no-docker", action="store_true", help="build on the host (unsafe for untrusted code)")
    args = parser.parse_args()

    work = args.work.resolve()
    if ROOT in work.parents or work == ROOT:
        sys.exit("--work must be outside the retryscope repository.")
    if not args.no_docker and shutil.which("docker") is None:
        sys.exit("Docker not found. Install Docker Desktop (free for personal use) and start it, then retry.")
    work.mkdir(parents=True, exist_ok=True)

    with LEDGER.open(newline="", encoding="utf-8") as handle:
        ledger = list(csv.DictReader(handle))
        fields = list(ledger[0].keys()) if ledger else []

    scope_rows: list[dict] = []
    for row in ledger:
        if row["status"] != "drawn":
            continue
        print(f"{row['stratum']} #{row['rank']} {row['repo']} @ {row['commit'][:7]}", flush=True)
        scope_rows.extend(prepare(row, work, use_docker=not args.no_docker))
        print(f"  -> {row['status']}: {row['reason']}", flush=True)

        # Save after every repository, so an interrupted run loses nothing.
        with LEDGER.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=fields)
            writer.writeheader()
            writer.writerows(ledger)
        new_file = not SCOPE.exists()
        with SCOPE.open("a", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=SCOPE_FIELDS)
            if new_file:
                writer.writeheader()
            writer.writerows(scope_rows)
        scope_rows = []


if __name__ == "__main__":
    main()
