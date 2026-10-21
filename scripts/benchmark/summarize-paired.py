#!/usr/bin/env python3
"""AI-006: summarizes a run of run-paired-benchmark.ps1 and compares it with the AI-004 baseline.

    python scripts/benchmark/summarize-paired.py --post docs/performance/post-be003 \
        --baseline-csv docs/performance/benchmark-results.csv \
        --baseline-summaries docs/performance/benchmark-summary-ollama.json docs/performance/benchmark-summary-backend.json \
        [--np2 docs/performance/numparallel-2] --output docs/performance/post-be003/summary.json

Percentiles use the nearest-rank rule of BenchmarkCommon.psm1, throughput is successful requests divided by
the wall time of the scenario, exactly as in AI-004, so the numbers can be compared directly.
Backend overhead is estimated from paired slices (Direct and Backend ran alternately in the same scenario).
"""
from __future__ import annotations

import argparse
import csv
import glob
import json
import math
import statistics
from collections import Counter, defaultdict
from pathlib import Path

T95 = {1: 12.71, 2: 4.30, 3: 3.18, 4: 2.78, 5: 2.57, 6: 2.45, 7: 2.36, 8: 2.31, 9: 2.26, 10: 2.23, 11: 2.20, 12: 2.18, 13: 2.16, 14: 2.14,
       15: 2.13, 16: 2.12, 17: 2.11, 18: 2.10, 19: 2.09, 20: 2.09, 25: 2.06, 30: 2.04}
THAI_LONG = ("long-3", "long-11", "long-14")


def nearest_rank(sorted_values, percent):
    if not sorted_values:
        return None
    rank = math.ceil(len(sorted_values) * percent / 100.0)
    return sorted_values[max(0, int(rank) - 1)]


def t_crit(df):
    if df <= 0:
        return None
    keys = sorted(T95)
    for k in keys:
        if df <= k:
            return T95[k]
    return 1.96


def read_rows(csv_path):
    rows = []
    with open(csv_path, encoding="utf-8", newline="") as fh:
        for r in csv.DictReader(fh):
            if r["phase"] == "measured":
                r["concurrency"] = int(r["concurrency"])
                r["elapsed_ms"] = float(r["elapsed_ms"])
                rows.append(r)
    return rows


def stats(rows):
    ok = sorted(r["elapsed_ms"] for r in rows if r["outcome"] == "success")
    outcomes = Counter(r["outcome"] for r in rows)
    codes = Counter(r["error_code"] for r in rows if r["outcome"] != "success" and r["error_code"])
    return {
        "requests": len(rows), "success": len(ok), "success_rate": round(len(ok) / len(rows), 4) if rows else None,
        "outcomes": dict(outcomes), "error_codes": dict(codes),
        "p50_ms": nearest_rank(ok, 50), "p95_ms": nearest_rank(ok, 95), "p99_ms": nearest_rank(ok, 99),
        "max_ms": ok[-1] if ok else None, "mean_ms": round(statistics.fmean(ok), 1) if ok else None, "p99_reliable": len(ok) >= 100,
    }


def group(rows):
    g = defaultdict(list)
    for r in rows:
        g[(r["target"], r["profile"], r["concurrency"])].append(r)
    return g


def slice_summaries(directory):
    """Per (target, profile, concurrency): wall seconds, successes and resource peaks from the per-slice JSON files."""
    agg = defaultdict(lambda: {"wall": 0.0, "success": 0, "slices": 0, "gpu_avg": [], "gpu_max": [], "vram_max": [], "ram_min": [], "cpu_avg": [], "cpu_max": [], "ollama_ws_max": []})
    run_ids = {}
    for f in sorted(glob.glob(str(Path(directory) / "slices" / "*.json"))):
        d = json.loads(Path(f).read_text(encoding="utf-8"))
        for sc in d["scenarios"]:
            key = (sc["target"], sc["profile"], sc["concurrency"])
            a = agg[key]
            a["wall"] += sc["wall_seconds"] or 0
            a["success"] += sc["success"] or 0
            a["slices"] += 1
            for src, dst in (("gpu_percent_avg", "gpu_avg"), ("gpu_percent_max", "gpu_max"), ("vram_used_mb_max", "vram_max"), ("ram_available_mb_min", "ram_min"),
                             ("cpu_percent_avg", "cpu_avg"), ("cpu_percent_max", "cpu_max"), ("ollama_working_set_mb_max", "ollama_ws_max")):
                if sc.get(src) is not None:
                    a[dst].append(sc[src])
            name = Path(f).stem  # profile-cN-NNN-target
            run_ids[d["run_id"]] = (sc["profile"], sc["concurrency"], int(name.split("-")[-2]), sc["target"])
    out = {}
    for key, a in agg.items():
        out[key] = {
            "wall_seconds": round(a["wall"], 1), "slices": a["slices"],
            "throughput_rps": round(a["success"] / a["wall"], 3) if a["wall"] else None,
            "gpu_percent_avg": round(statistics.fmean(a["gpu_avg"]), 1) if a["gpu_avg"] else None,
            "gpu_percent_max": max(a["gpu_max"]) if a["gpu_max"] else None,
            "vram_used_mb_max": max(a["vram_max"]) if a["vram_max"] else None,
            "ram_available_mb_min": min(a["ram_min"]) if a["ram_min"] else None,
            "cpu_percent_avg": round(statistics.fmean(a["cpu_avg"]), 1) if a["cpu_avg"] else None,
            "cpu_percent_max": max(a["cpu_max"]) if a["cpu_max"] else None,
            "ollama_working_set_mb_max": max(a["ollama_ws_max"]) if a["ollama_ws_max"] else None,
        }
    return out, run_ids


def paired_overhead(rows, run_ids):
    """Backend P50 minus Direct P50 for every slice pair of one scenario."""
    per = defaultdict(dict)
    by_run = defaultdict(list)
    for r in rows:
        by_run[r["run_id"]].append(r)
    for run_id, rs in by_run.items():
        if run_id not in run_ids:
            continue
        profile, c, k, target = run_ids[run_id]
        ok = sorted(x["elapsed_ms"] for x in rs if x["outcome"] == "success")
        if ok:
            per[(profile, c)].setdefault(k, {})[target] = nearest_rank(ok, 50)
    out = {}
    for key, slices in per.items():
        diffs = [v["backend"] - v["ollama"] for v in slices.values() if "backend" in v and "ollama" in v]
        if len(diffs) < 2:
            continue
        mean, sd = statistics.fmean(diffs), statistics.stdev(diffs)
        half = t_crit(len(diffs) - 1) * sd / math.sqrt(len(diffs))
        direct = statistics.fmean([v["ollama"] for v in slices.values() if "ollama" in v and "backend" in v])
        out[key] = {"pairs": len(diffs), "mean_diff_ms": round(mean, 1), "sd_ms": round(sd, 1), "ci95_low_ms": round(mean - half, 1), "ci95_high_ms": round(mean + half, 1),
                    "direct_mean_p50_ms": round(direct, 1), "separable_from_zero": (mean - half) > 0 or (mean + half) < 0}
    return out


def baseline_throughput(paths):
    out = {}
    for p in paths:
        d = json.loads(Path(p).read_text(encoding="utf-8"))
        for sc in d["scenarios"]:
            out[(sc["target"], sc["profile"], sc["concurrency"])] = sc["throughput_rps"]
    return out


def fmt(v, digits=0):
    if v is None:
        return "-"
    return f"{v:,.{digits}f}"


def pct(a, b):
    if a in (None, 0) or b is None:
        return "-"
    return f"{(b - a) / a * 100:+.0f}%"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--post", required=True)
    ap.add_argument("--baseline-csv", required=True)
    ap.add_argument("--baseline-summaries", nargs="+", required=True)
    ap.add_argument("--np2")
    ap.add_argument("--output", required=True)
    args = ap.parse_args(argv)

    post_rows = read_rows(Path(args.post) / "benchmark-results.csv")
    base_rows = read_rows(args.baseline_csv)
    post_g, base_g = group(post_rows), group(base_rows)
    post_sum, run_ids = slice_summaries(args.post)
    base_thr = baseline_throughput(args.baseline_summaries)
    result = {"scenarios": [], "overhead": [], "thai_long": [], "np2": []}
    scen_keys = sorted(post_g, key=lambda k: (k[1], k[2], k[0]))
    for key in scen_keys:
        target, profile, c = key
        p, b = stats(post_g[key]), stats(base_g.get(key, []))
        result["scenarios"].append({"target": target, "profile": profile, "concurrency": c, "post": {**p, **post_sum.get(key, {})},
                                    "baseline": {**b, "throughput_rps": base_thr.get(key)}})
    for (profile, c), o in sorted(paired_overhead(post_rows, run_ids).items(), key=lambda x: (x[0][0], x[0][1])):
        result["overhead"].append({"profile": profile, "concurrency": c, **o})
    for target in ("ollama", "backend"):
        for pid in THAI_LONG:
            post = [r for r in post_rows if r["target"] == target and r["prompt_id"] == pid]
            base = [r for r in base_rows if r["target"] == target and r["prompt_id"] == pid]
            result["thai_long"].append({"target": target, "prompt_id": pid, "baseline": dict(Counter(r["outcome"] for r in base)),
                                        "baseline_codes": dict(Counter(r["error_code"] for r in base if r["error_code"])),
                                        "post": dict(Counter(r["outcome"] for r in post)), "post_codes": dict(Counter(r["error_code"] for r in post if r["error_code"]))})
    if args.np2:
        np2_rows = read_rows(Path(args.np2) / "benchmark-results.csv")
        np2_g = group(np2_rows)
        np2_sum, _ = slice_summaries(args.np2)
        for key in sorted(np2_g, key=lambda k: (k[1], k[2])):
            p1 = {**stats(post_g.get(key, [])), **post_sum.get(key, {})} if key in post_g else None
            p2 = {**stats(np2_g[key]), **np2_sum.get(key, {})}
            result["np2"].append({"profile": key[1], "concurrency": key[2], "np1": p1, "np2": p2})

    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2, default=str), encoding="utf-8")

    md = []
    md += ["### ผลหลัง BE-003 เทียบ baseline AI-004 (P50/P95/P99 เป็น ms, throughput เป็นคำขอสำเร็จต่อวินาที)", "",
           "| เป้าหมาย | Profile | Conc. | n (หลัง) | Success (ก่อน → หลัง) | P50 ก่อน → หลัง | P95 ก่อน → หลัง | P99 หลัง | req/s ก่อน → หลัง |",
           "| --- | --- | ---: | ---: | --- | --- | --- | ---: | --- |"]
    for s in result["scenarios"]:
        p, b = s["post"], s["baseline"]
        md.append(f"| {s['target']} | {s['profile']} | {s['concurrency']} | {p['success']}/{p['requests']} | {fmt((b['success_rate'] or 0) * 100)}% → {fmt((p['success_rate'] or 0) * 100)}% | "
                  f"{fmt(b['p50_ms'])} → {fmt(p['p50_ms'])} ({pct(b['p50_ms'], p['p50_ms'])}) | {fmt(b['p95_ms'])} → {fmt(p['p95_ms'])} ({pct(b['p95_ms'], p['p95_ms'])}) | {fmt(p['p99_ms'])} | "
                  f"{fmt(b['throughput_rps'], 2)} → {fmt(p.get('throughput_rps'), 2)} |")
    md += ["", "### ส่วนต่าง P50 ของ Backend เทียบ Direct จาก slice ที่สลับกัน (ms)", "",
           "| Profile | Conc. | คู่ slice | Direct P50 เฉลี่ย | ส่วนต่างเฉลี่ย | SD | ช่วงเชื่อมั่น 95% | แยกจากศูนย์ได้ |", "| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |"]
    for o in result["overhead"]:
        md.append(f"| {o['profile']} | {o['concurrency']} | {o['pairs']} | {fmt(o['direct_mean_p50_ms'])} | {o['mean_diff_ms']:+.1f} | {o['sd_ms']:.1f} | {o['ci95_low_ms']:+.1f} ถึง {o['ci95_high_ms']:+.1f} | {'ใช่' if o['separable_from_zero'] else 'ไม่'} |")
    md += ["", "### prompt ภาษาไทยของ profile long ที่เคยถูกตัดที่ 256 token", "", "| เป้าหมาย | Prompt | ก่อน BE-003 | หลัง BE-003 |", "| --- | --- | --- | --- |"]
    for t in result["thai_long"]:
        md.append(f"| {t['target']} | {t['prompt_id']} | {t['baseline']} {t['baseline_codes'] or ''} | {t['post']} {t['post_codes'] or ''} |")
    md += ["", "### ทรัพยากรสูงสุดหลัง BE-003", "", "| เป้าหมาย | Profile | Conc. | GPU เฉลี่ย/สูงสุด % | VRAM สูงสุด MB | RAM ว่างต่ำสุด MB | Ollama working set สูงสุด MB |", "| --- | --- | ---: | --- | ---: | ---: | ---: |"]
    for s in result["scenarios"]:
        p = s["post"]
        md.append(f"| {s['target']} | {s['profile']} | {s['concurrency']} | {fmt(p.get('gpu_percent_avg'), 1)} / {fmt(p.get('gpu_percent_max'))} | {fmt(p.get('vram_used_mb_max'))} | {fmt(p.get('ram_available_mb_min'))} | {fmt(p.get('ollama_working_set_mb_max'))} |")
    if result["np2"]:
        md += ["", "### OLLAMA_NUM_PARALLEL=2 เทียบ 1 (Direct เท่านั้น)", "",
               "| Profile | Conc. | n (NP2) | Success NP1 → NP2 | P50 NP1 → NP2 | P95 NP1 → NP2 | req/s NP1 → NP2 | VRAM สูงสุด NP1 → NP2 | RAM ว่างต่ำสุด NP1 → NP2 |", "| --- | ---: | ---: | --- | --- | --- | --- | --- | --- |"]
        for e in result["np2"]:
            a, b = e["np1"] or {}, e["np2"]
            md.append(f"| {e['profile']} | {e['concurrency']} | {b['success']}/{b['requests']} | {fmt((a.get('success_rate') or 0) * 100)}% → {fmt((b['success_rate'] or 0) * 100)}% | "
                      f"{fmt(a.get('p50_ms'))} → {fmt(b['p50_ms'])} ({pct(a.get('p50_ms'), b['p50_ms'])}) | {fmt(a.get('p95_ms'))} → {fmt(b['p95_ms'])} ({pct(a.get('p95_ms'), b['p95_ms'])}) | "
                      f"{fmt(a.get('throughput_rps'), 2)} → {fmt(b.get('throughput_rps'), 2)} | {fmt(a.get('vram_used_mb_max'))} → {fmt(b.get('vram_used_mb_max'))} | {fmt(a.get('ram_available_mb_min'))} → {fmt(b.get('ram_available_mb_min'))} |")
    (Path(args.output).with_suffix(".md")).write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"Wrote {args.output} and {Path(args.output).with_suffix('.md')}")


if __name__ == "__main__":
    main()
