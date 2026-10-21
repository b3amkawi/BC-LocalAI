#!/usr/bin/env python3
"""Runs the QA-002 authentication/authorization suite and merges the evidence.

The suite is BC-LocalAI.Tests/Qa002 (WebApplicationFactory against the real pipeline, fake Ollama, synthetic accounts).
Each run is a separate `dotnet test` process, one test collection at a time (moderate CPU). Every test records its
Expected (from tests/security/qa002-cases.json, written before the first run) and its Actual in one JSON line.

Usage:
    python tests/security/run_qa002.py                       # 2 runs -> docs/qa/qa-002-evidence.json
    python tests/security/run_qa002.py --runs 3 --no-build
    python tests/security/run_qa002.py --merge-only DIR      # merge run-*.jsonl files already in DIR

Exit code 0 when every case that is not a documented known defect passed in every run, 1 otherwise.
Evidence holds statuses, error codes and short notes only: no cookie, token, password or hash is ever written.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import platform
import subprocess
import sys
import tempfile
from collections import OrderedDict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "tests" / "security" / "qa002-cases.json"
KNOWN = ROOT / "tests" / "security" / "qa002-known-defects.json"
DEFAULT_OUTPUT = ROOT / "docs" / "qa" / "qa-002-evidence.json"


def load_known() -> dict:
    if not KNOWN.exists():
        return {}
    return {k: v for k, v in json.loads(KNOWN.read_text(encoding="utf-8")).items() if not k.startswith("_")}


def run_once(number: int, workdir: Path, build: bool) -> tuple[Path, int, str]:
    evidence = workdir / f"run-{number}.jsonl"
    if evidence.exists():
        evidence.unlink()
    env = dict(os.environ)
    env.update({"QA002_RUN": str(number), "QA002_EVIDENCE": str(evidence)})
    cmd = ["dotnet", "test", str(ROOT / "BC-LocalAI.Tests"), "--nologo", "--filter", "FullyQualifiedName~Qa002"]
    if not build:
        cmd.append("--no-build")
    cmd += ["--", "xUnit.ParallelizeTestCollections=false"]
    proc = subprocess.run(cmd, cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    summary = next((line.strip() for line in reversed(proc.stdout.splitlines()) if "Passed!" in line or "Failed!" in line), "(no summary line)")
    return evidence, proc.returncode, summary


def read_lines(path: Path) -> list[dict]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def merge(files: list[Path], dotnet_summaries: list[str]) -> dict:
    catalog = json.loads(CASES.read_text(encoding="utf-8"))
    known = load_known()
    runs = len(files)
    by_case: dict[str, list[dict]] = OrderedDict((c["id"], []) for c in catalog["cases"])
    for path in files:
        for line in read_lines(path):
            if line["id"] in by_case:
                by_case[line["id"]].append(line)
    cases = []
    for cid, lines in by_case.items():
        if not lines:
            continue
        verdicts = [line["verdict"] for line in lines]
        last = lines[-1]
        if all(v == "OBSERVED" for v in verdicts):
            verdict = "OBSERVED"
        elif all(v == "PASS" for v in verdicts):
            verdict = "PASS"
        else:
            verdict = "FAIL"
        same = len({(line["actualStatus"], line["actualCode"], line["actualOllama"]) for line in lines}) == 1
        entry = {
            "id": cid,
            "verdict": verdict,
            "runs": runs,
            "recordedRuns": len(lines),
            "passedRuns": sum(1 for v in verdicts if v == "PASS"),
            "actualStatus": last["actualStatus"],
            "actualCode": last["actualCode"],
            "actualOllama": last["actualOllama"],
            "consistentAcrossRuns": same,
            "detail": last["detail"],
            "defect": known.get(cid) or last.get("defect"),
        }
        cases.append(entry)
    ids = {c["id"] for c in cases}
    missing = [c["id"] for c in catalog["cases"] if c["id"] not in ids]
    summary = {
        "catalogCases": len(catalog["cases"]),
        "executedCases": len(cases),
        "notExecuted": missing,
        "pass": sum(1 for c in cases if c["verdict"] == "PASS"),
        "fail": sum(1 for c in cases if c["verdict"] == "FAIL"),
        "failWithKnownDefect": sum(1 for c in cases if c["verdict"] == "FAIL" and c["defect"]),
        "failWithoutDefect": [c["id"] for c in cases if c["verdict"] == "FAIL" and not c["defect"]],
        "observed": sum(1 for c in cases if c["verdict"] == "OBSERVED"),
        "inconsistentAcrossRuns": [c["id"] for c in cases if not c["consistentAcrossRuns"]],
        "caseRuns": sum(c["recordedRuns"] for c in cases),
    }
    return {
        "suite": "QA-002 authentication and authorization",
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
        "environment": {"os": platform.platform(), "python": platform.python_version(), "dotnet": dotnet_version()},
        "method": "WebApplicationFactory<Program> (real pipeline over HTTPS), fake Ollama request counter, synthetic accounts, manual clock",
        "runs": runs,
        "dotnetSummaries": dotnet_summaries,
        "summary": summary,
        "cases": cases,
    }


def dotnet_version() -> str:
    try:
        return subprocess.run(["dotnet", "--version"], capture_output=True, text=True, timeout=30).stdout.strip()
    except Exception:  # noqa: BLE001
        return "unknown"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--runs", type=int, default=2)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--merge-only", type=Path)
    args = parser.parse_args(argv)

    summaries: list[str] = []
    if args.merge_only:
        files = sorted(args.merge_only.glob("run-*.jsonl"))
    else:
        workdir = Path(tempfile.mkdtemp(prefix="qa002-"))
        files = []
        for number in range(1, args.runs + 1):
            print(f"run {number}/{args.runs} ...", flush=True)
            path, code, summary = run_once(number, workdir, build=(number == 1 and not args.no_build))
            print(f"  dotnet exit {code}: {summary}", flush=True)
            files.append(path)
            summaries.append(f"run {number}: {summary}")
    result = merge(files, summaries)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    s = result["summary"]
    print(f"{s['executedCases']}/{s['catalogCases']} cases, {s['caseRuns']} case-runs: pass {s['pass']}, fail {s['fail']} "
          f"({s['failWithKnownDefect']} documented defects), observed {s['observed']}; not executed {len(s['notExecuted'])}")
    if s["failWithoutDefect"] or s["notExecuted"]:
        print("unexpected:", s["failWithoutDefect"], s["notExecuted"])
    return 0 if not s["failWithoutDefect"] and not s["notExecuted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
