#!/usr/bin/env python3
"""Quality gate: every test suite must pass, none may be skipped, and the number of
tests that ran must not drop below the recorded baseline.

Usage:
    python scripts/ci/check_test_counts.py --baseline docs/devops/test-baseline.json \
        --dotnet dotnet.log --node node.log --python python.log

The baseline lowers only by editing docs/devops/test-baseline.json, so a reduction is
visible in the pull request diff and needs a reason in the description.
Exit code: 0 all gates pass, 1 a gate failed, 2 a log could not be parsed.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

DOTNET_RE = re.compile(
    r"(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)"
)
NODE_RE = {
    "tests": re.compile(r"^\D*tests\s+(\d+)\s*$", re.M),
    "fail": re.compile(r"^\D*fail\s+(\d+)\s*$", re.M),
    "skipped": re.compile(r"^\D*skipped\s+(\d+)\s*$", re.M),
    "todo": re.compile(r"^\D*todo\s+(\d+)\s*$", re.M),
}
PY_RAN = re.compile(r"^Ran (\d+) tests? in ", re.M)
PY_RESULT = re.compile(r"^(OK|FAILED)(?: \(([^)]*)\))?\s*$", re.M)
PY_SKIP = re.compile(r"^(\w+) \([^)]*\) \.\.\. skipped ", re.M)


class ParseError(Exception):
    pass


def parse_dotnet(text: str) -> dict:
    rows = DOTNET_RE.findall(text)
    if not rows:
        raise ParseError("no 'Passed!/Failed!' summary line in the dotnet log")
    failed = sum(int(r[0]) for r in rows)
    skipped = sum(int(r[2]) for r in rows)
    total = sum(int(r[3]) for r in rows)
    return {"total": total, "failed": failed, "skipped": skipped}


def parse_node(text: str) -> dict:
    found = {}
    for key, rx in NODE_RE.items():
        matches = rx.findall(text)
        if matches:
            found[key] = int(matches[-1])
    if "tests" not in found:
        raise ParseError("no 'tests N' summary line in the node log")
    return {
        "total": found["tests"],
        "failed": found.get("fail", 0),
        "skipped": found.get("skipped", 0) + found.get("todo", 0),
    }


def parse_python(text: str) -> dict:
    ran = PY_RAN.findall(text)
    result = PY_RESULT.findall(text)
    if not ran or not result:
        raise ParseError("no 'Ran N tests' / OK|FAILED summary in the python log")
    status, detail = result[-1]
    counts = {}
    for part in (detail or "").split(","):
        part = part.strip()
        if "=" in part:
            name, value = part.split("=", 1)
            counts[name.strip()] = int(value)
    failed = counts.get("failures", 0) + counts.get("errors", 0)
    if status == "FAILED" and failed == 0:
        failed = 1
    return {
        "total": int(ran[-1]), "failed": failed, "skipped": counts.get("skipped", 0),
        "skipped_tests": PY_SKIP.findall(text),  # names, available when the log is verbose (-v)
    }


PARSERS = {"dotnet": parse_dotnet, "node": parse_node, "python": parse_python}


def evaluate(baseline: dict, results: dict) -> tuple[list[str], list[str]]:
    """Return (failures, table_rows)."""
    failures, rows = [], []
    allowed_skipped = baseline.get("allowed_skipped", 0)
    named_skips = baseline.get("allowed_skips", {})   # suite -> test names that may be skipped on a clean machine
    for name in ("dotnet", "node", "python"):
        minimum = baseline["min_tests"][name]
        res = results.get(name)
        if res is None:
            failures.append(f"{name}: no result was provided")
            rows.append(f"| {name} | missing | {minimum} | - | - | FAIL |")
            continue
        problems = []
        if res["failed"] > 0:
            problems.append(f"{res['failed']} failed")
        if res["total"] < minimum:
            problems.append(f"only {res['total']} tests ran, baseline is {minimum}")
        allowed_names = set(named_skips.get(name, []))
        if allowed_names and res.get("skipped_tests") is not None and res["skipped"] == len(res["skipped_tests"]):
            unexpected = [t for t in res["skipped_tests"] if t not in allowed_names]
            if unexpected:
                problems.append(f"unexpected skipped test(s): {', '.join(unexpected)}")
        elif res["skipped"] > allowed_skipped + len(allowed_names):
            problems.append(f"{res['skipped']} skipped (allowed {allowed_skipped + len(allowed_names)})")
        verdict = "FAIL" if problems else "pass"
        rows.append(f"| {name} | {res['total']} | {minimum} | {res['failed']} | {res['skipped']} | {verdict} |")
        for p in problems:
            failures.append(f"{name}: {p}")
    return failures, rows


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--baseline", required=True)
    for name in PARSERS:
        ap.add_argument(f"--{name}", required=True, help=f"log file of the {name} test run")
    args = ap.parse_args(argv)

    baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
    results = {}
    for name, parser in PARSERS.items():
        try:
            results[name] = parser(Path(getattr(args, name)).read_text(encoding="utf-8", errors="replace"))
        except (ParseError, OSError) as exc:
            print(f"ERROR: cannot read the {name} result: {exc}", file=sys.stderr)
            return 2

    failures, rows = evaluate(baseline, results)
    table = ["| suite | tests ran | baseline | failed | skipped | gate |", "| --- | ---: | ---: | ---: | ---: | --- |"] + rows
    print("\n".join(table))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write("### Test count gate\n\n" + "\n".join(table) + "\n")
    if failures:
        print("\nGate failed:", file=sys.stderr)
        for f in failures:
            print(f"  - {f}", file=sys.stderr)
        return 1
    print("\nAll test gates passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
