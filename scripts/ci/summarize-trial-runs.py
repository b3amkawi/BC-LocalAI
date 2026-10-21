#!/usr/bin/env python3
"""Summarizes the CI runs of the ci-trial/** branches with the GitHub CLI (read-only).

    python scripts/ci/summarize-trial-runs.py --repo owner/name > table.md

Each trial branch carries one deliberate defect; the table shows which job and step stopped the run.
"""
import argparse
import json
import subprocess
from datetime import datetime


def gh(*args):
    return json.loads(subprocess.run(["gh", *args], capture_output=True, text=True, check=True).stdout)


def seconds(start, end):
    fmt = "%Y-%m-%dT%H:%M:%SZ"
    return int((datetime.strptime(end, fmt) - datetime.strptime(start, fmt)).total_seconds())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--limit", type=int, default=40)
    args = ap.parse_args()
    runs = gh("run", "list", "--repo", args.repo, "--limit", str(args.limit), "--json", "databaseId,headBranch,status,conclusion,createdAt")
    rows = []
    for run in runs:
        if not run["headBranch"].startswith("ci-trial/"):
            continue
        detail = gh("run", "view", str(run["databaseId"]), "--repo", args.repo, "--json", "jobs")
        stopped = []
        times = {}
        for job in detail["jobs"]:
            if job["conclusion"] == "failure":
                steps = [s["name"] for s in job["steps"] if s["conclusion"] == "failure"]
                stopped.append(f"{job['name']}: {', '.join(steps)}")
            if job.get("completedAt") and job.get("startedAt") and job["completedAt"] > job["startedAt"]:
                times[job["name"][:5]] = seconds(job["startedAt"], job["completedAt"])
        rows.append((run["createdAt"], run["headBranch"].removeprefix("ci-trial/"), run["conclusion"], "; ".join(stopped) or "-", times, run["databaseId"]))
    rows.sort()
    print("| Branch | ผลของ run | job/step ที่หยุด | เวลา Windows job | เวลา hygiene job |")
    print("| --- | --- | --- | ---: | ---: |")
    for _, branch, conclusion, stopped, times, run_id in rows:
        win = next((v for k, v in times.items() if k.startswith("Build")), None)
        hyg = next((v for k, v in times.items() if k.startswith("Secre")), None)
        fmt = lambda v: "-" if v is None else f"{v // 60} นาที {v % 60} วินาที"
        print(f"| `{branch}` (run {run_id}) | {conclusion} | {stopped} | {fmt(win)} | {fmt(hyg)} |")


if __name__ == "__main__":
    main()
