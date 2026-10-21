#!/usr/bin/env python3
"""AI-006: removes slices that were disturbed by the machine (sleep, long stall) from a run-paired-benchmark.ps1 result.

    python scripts/benchmark/drop-contaminated-slices.py docs/performance/post-be003 [--dry-run]

A slice is contaminated when one of its requests took more than 120 s AND more than 5 times the median latency of
its scenario and target (a request that really needs minutes shows up in every slice, so it is not flagged). Its
partner slice (same profile, concurrency and slice number, other target) is dropped too so the two targets keep the
same number of slices. Dropped slices are listed in contamination-dropped.json; run-paired-benchmark.ps1 then
refills the scenario when it is started again, because it counts the measured rows still in the CSV.
"""
import argparse
import csv
import json
import statistics
from collections import defaultdict
from pathlib import Path


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("directory")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--drop-after", metavar="YYYYMMDDTHHMMSS", help="also drop every slice that started at or after this local time (e.g. the charger was unplugged: the GPU runs at a lower power limit on battery)")
    args = ap.parse_args()
    d = Path(args.directory)
    csv_path = d / "benchmark-results.csv"
    with open(csv_path, encoding="utf-8", newline="") as fh:
        reader = csv.DictReader(fh)
        fields = reader.fieldnames
        rows = list(reader)

    slice_of = {}   # run_id -> (profile, concurrency, k, target, json path)
    for f in sorted((d / "slices").glob("*.json")):
        j = json.loads(f.read_text(encoding="utf-8"))
        sc = j["scenarios"][0]
        slice_of[j["run_id"]] = (sc["profile"], int(sc["concurrency"]), int(f.stem.split("-")[-2]), sc["target"], f)

    lat = defaultdict(list)
    for r in rows:
        if r["phase"] == "measured" and r["outcome"] == "success":
            lat[(r["target"], r["profile"], int(r["concurrency"]))].append(float(r["elapsed_ms"]))
    median = {k: statistics.median(v) for k, v in lat.items()}

    flagged = {}
    for r in rows:
        if r["phase"] != "measured":
            continue
        key = (r["target"], r["profile"], int(r["concurrency"]))
        e = float(r["elapsed_ms"])
        if e > 120000 and e > 5 * median.get(key, e):
            flagged.setdefault(r["run_id"], []).append(round(e))
    drop_runs = set(flagged)
    reasons = {}
    if args.drop_after:
        for run in slice_of:
            if run.split("-")[0] >= args.drop_after:
                flagged.setdefault(run, [])
                reasons[run] = f"started after {args.drop_after} (machine on battery)"
                drop_runs.add(run)
    for run in list(flagged):
        if run not in slice_of:
            continue
        profile, c, k, target, _ = slice_of[run]
        reasons.setdefault(run, f"request(s) of {flagged[run]} ms (scenario median {round(median[(target, profile, c)])} ms)")
        for other, (p2, c2, k2, t2, _) in slice_of.items():
            if (p2, c2, k2) == (profile, c, k) and other != run:
                drop_runs.add(other)
                reasons.setdefault(other, f"partner of {run}")
    report = [{"run_id": r, "slice": slice_of[r][4].name if r in slice_of else None, "reason": reasons.get(r, "flagged"),
               "rows": sum(1 for x in rows if x["run_id"] == r)} for r in sorted(drop_runs)]
    print(json.dumps(report, ensure_ascii=False, indent=1))
    if args.dry_run or not drop_runs:
        print("nothing changed" if args.dry_run else "no contaminated slices found")
        return
    kept = [r for r in rows if r["run_id"] not in drop_runs]
    with open(csv_path, "w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        w.writerows(kept)
    for r in drop_runs:
        if r in slice_of:
            slice_of[r][4].unlink()
    log = d / "contamination-dropped.json"
    earlier = json.loads(log.read_text(encoding="utf-8"))["dropped"] if log.exists() else []   # keep the record of earlier drops
    report = earlier + report
    log.write_text(json.dumps({"dropped": report, "rule": "request > 120 s and > 5x the scenario median; or slice started after the --drop-after time"}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"dropped {len(drop_runs)} slices, {len(rows) - len(kept)} rows")


if __name__ == "__main__":
    main()
