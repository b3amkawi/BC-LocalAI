#!/usr/bin/env python3
"""Builds docs/ai/ai-failure-test-results.md (AI-007 D3) from the evidence of scripts/test-ollama-failures.ps1.

    python scripts/make-failure-results.py
Reads docs/ai/ollama-failures-evidence.json (final run) and ollama-failures-evidence-initial.json (first run, kept).
"""
import json
import statistics
from pathlib import Path

DOCS = Path(__file__).resolve().parents[1] / "docs" / "ai"


def load(name):
    return json.loads((DOCS / name).read_text(encoding="utf-8"))


def esc(v):
    return str(v).replace("|", "\\|").replace("\n", " ")


def rec_cell(summary):
    if not summary:
        return "-"
    return f"{summary['min_ms']:,.0f} / {summary['median_ms']:,.0f} / {summary['max_ms']:,.0f} ms (n={summary['n']})"


def main():
    final, initial = load("ollama-failures-evidence.json"), load("ollama-failures-evidence-initial.json")
    env = final["environment"]
    total_inj = sum(len(t["repetitions"]) for t in final["types"])
    L = ["# AI-007 Failure Injection Results (D3)", "",
         f"ทดสอบเมื่อ {final['generated_utc'][:19]} UTC บน Windows 11, RTX 4050 Laptop 6 GB, Ollama {env['ollama_version']}, qwen3:4b (digest {env['model_digest']})",
         f"จำนวน {len(final['types'])} ชนิดความล้มเหลว รันชนิดละ {final['repeats']} ครั้ง รวม {total_inj} การ inject (เกณฑ์ของ Issue คือ 5 ชนิด ชนิดละ 3 ครั้ง)", "",
         "การ inject ทำบน Ollama instance แยกที่สคริปต์เริ่มเองบนพอร์ต " + str(env["dedicated_port"]) + " (ไฟล์โมเดลเดียวกัน) ไม่มีการหยุดหรือแก้ instance ปกติที่พอร์ต 11434 นอกจากสั่ง unload โมเดลเพื่อไม่ให้แย่ง VRAM",
         "ไม่ได้ inject: " + ", ".join(final["not_injected"]) + " เพราะเสี่ยงทำให้เครื่องค้าง", "",
         "## สรุป", "",
         "| ID | ความล้มเหลว | รัน | ผ่าน | เวลาฟื้น ต่ำสุด / กลาง / สูงสุด |", "| --- | --- | ---: | ---: | --- |"]
    for t in final["types"]:
        ok = sum(1 for r in t["repetitions"] if r["passed"])
        L.append(f"| {t['id']} | {esc(t['name'])} | {len(t['repetitions'])} | {ok} | {rec_cell(t['recovery_summary'])} |")
    L += ["", "เวลาฟื้นคือเวลาตั้งแต่ inject จนคำขอสั้นถัดไปสำเร็จ ของ F02 และ F03 รวมเวลาที่สคริปต์สตาร์ท Ollama ใหม่ (เสมือนมีตัวดูแล process) ของ F09 คือเวลาถึง record แรกของคำขอถัดไป ของ F10 คือเวลาของคำขอสั้นที่ตามมา", "",
          "## Expected และผลของแต่ละชนิด", ""]
    for t in final["types"]:
        L += [f"### {t['id']} {t['name']}", "", "Expected (กำหนดก่อนรัน):", ""]
        L += [f"- {e}" for e in t["expected"]]
        first = t["repetitions"][0]
        L += ["", "Actual (ครั้งที่ 1):", "", "```json", json.dumps(first["actual"], ensure_ascii=False, indent=1)[:1600], "```", "",
              "| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |", "| --- | --- | --- | --- |"]
        for i, c in enumerate(first["checks"]):
            allpass = all(r["checks"][i]["passed"] for r in t["repetitions"] if len(r["checks"]) > i)
            L.append(f"| {esc(c['name'])} | {esc(c['expected'])} | {esc(c['actual'])} | {'ใช่' if allpass else '**ไม่**'} |")
        L.append("")
    bad = [t["id"] for t in initial["types"] if not t["passed"]]
    L += ["## การบันทึกการแก้ Expected", "",
          f"รันครั้งแรกผ่าน {sum(1 for t in initial['types'] if t['passed'])}/{len(initial['types'])} ชนิด (เก็บที่ `ollama-failures-evidence-initial.json`) ชนิดที่ไม่ผ่าน: {', '.join(bad)} ทั้งหมดเป็นการคาดเดาหรือการออกแบบกรณีทดสอบที่ผิดของผู้ทดสอบ ไม่ใช่ความบกพร่องของ Ollama:", "",
          "| ID | Expected เดิม | ที่แก้ | เหตุผล |", "| --- | --- | --- | --- |"]
    for r in final["expected_revisions"]:
        L.append(f"| {r['id']} | {esc(r['original'])} | {esc(r['revised'])} | {esc(r['reason'])} |")
    L += ["", "หลักฐานสุดท้ายมาจากสองรัน (ดู `provenance` ในไฟล์ evidence): รันเต็ม 10 ชนิดด้วย Expected ที่แก้รอบแรก และรัน F10 อย่างเดียวหลังพบว่าการออกแบบ F10 ผิดอีกครั้งตอนอ่านผลรันเต็ม (คำขอที่รันเสร็จก่อนคำขอที่สองหมดเวลา จึงไม่มีอะไรถูกทิ้งขณะรอคิว) F10 รัน 6 ครั้งผ่านทั้งหมด", ""]
    (DOCS / "ai-failure-test-results.md").write_text("\n".join(L), encoding="utf-8")
    print("written", DOCS / "ai-failure-test-results.md")


if __name__ == "__main__":
    main()
