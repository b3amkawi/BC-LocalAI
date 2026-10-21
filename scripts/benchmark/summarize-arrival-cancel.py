#!/usr/bin/env python3
"""AI-006: Markdown tables from the Arrival and Cancel result files of arrival-and-cancel-benchmark.ps1.

    python scripts/benchmark/summarize-arrival-cancel.py docs/performance/post-be003 > docs/performance/post-be003/arrival-cancel-summary.md
"""
import json
import sys
from pathlib import Path


def load(folder):
    return [json.loads(f.read_text(encoding="utf-8")) for f in sorted(folder.glob("*.json"))]


def main():
    base = Path(sys.argv[1])
    arrival = load(base / "arrival")
    cancel = load(base / "cancel")
    order = {"short": 0, "medium": 1, "long": 2}
    print("### Random arrivals (Poisson), latency includes queue wait (ms)\n")
    print("| Profile | Rate req/s | Target | Arrived | Success | Unfinished | P50 | P95 | P99 | Max | P50 first quarter | P50 last quarter |")
    print("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for r in sorted(arrival, key=lambda r: (order[r["profile"]], r["rate_rps"], r["target"])):
        print(f"| {r['profile']} | {r['rate_rps']} | {r['target']} | {r['arrived']} | {r['success']} | {r['unfinished']} | {r['latency_p50_ms']:,.0f} | "
              f"{r['latency_p95_ms']:,.0f} | {r['latency_p99_ms']:,.0f} | {r['latency_max_ms']:,.0f} | {r['latency_p50_first_quarter_ms']:,.0f} | {r['latency_p50_last_quarter_ms']:,.0f} |")
    print("\nP50 last quarter much higher than first quarter = the queue was still growing (arrival rate above capacity).\n")
    print("### Cancel while queued: time until the remaining requests finish (ms, median of repeats)\n")
    print("| Profile | Target | Burst | Cancelled | Repeats | No cancel (min-max) | With cancel (min-max) | Saved |")
    print("| --- | --- | ---: | ---: | ---: | --- | --- | ---: |")
    for r in sorted(cancel, key=lambda r: (order[r["profile"]], r["target"])):
        saved = 1 - r["cancel_median_ms"] / r["control_median_ms"]
        print(f"| {r['profile']} | {r['target']} | {r['burst']} | {r['cancel_count']} | {r['repeats']} | {r['control_median_ms']:,.0f} ({r['control_min_ms']:,.0f}-{r['control_max_ms']:,.0f}) | "
              f"{r['cancel_median_ms']:,.0f} ({r['cancel_min_ms']:,.0f}-{r['cancel_max_ms']:,.0f}) | {saved:.0%} |")


if __name__ == "__main__":
    main()
