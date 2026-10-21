#!/usr/bin/env python3
"""Renders the QA-002 documents from the case catalog and the evidence.

    python tests/security/make_qa002_docs.py matrix    # docs/qa/qa-002-test-matrix.md (Expected only)
    python tests/security/make_qa002_docs.py results   # docs/qa/qa-002-results-table.md (Expected vs Actual per case)

The matrix is generated from tests/security/qa002-cases.json (written before any run). The results table reads
docs/qa/qa-002-evidence.json, which the test run writes; qa-002-results.md links to it.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "tests" / "security" / "qa002-cases.json"
EVIDENCE = ROOT / "docs" / "qa" / "qa-002-evidence.json"
MATRIX_MD = ROOT / "docs" / "qa" / "qa-002-test-matrix.md"
TABLE_MD = ROOT / "docs" / "qa" / "qa-002-results-table.md"


def cell(text) -> str:
    return str(text if text is not None else "-").replace("|", "\\|").replace("\n", " ")


def load_catalog() -> dict:
    return json.loads(CASES.read_text(encoding="utf-8"))


def matrix() -> str:
    data = load_catalog()
    cases = data["cases"]
    out = [
        "# QA-002 Authentication and Authorization Test Matrix (D1)",
        "",
        "ตาราง role × endpoint และกรณีทดสอบ authentication/authorization ของทั้งระบบ **Expected ทุกกรณีเขียนไว้ก่อนรันและเก็บใน "
        "[qa002-cases.json](../../tests/security/qa002-cases.json)** (สร้างจาก [qa002_cases.py](../../tests/security/qa002_cases.py)) "
        "ไฟล์นี้สร้างจาก catalog เดียวกัน ผล Actual บันทึกแยกใน [qa-002-evidence.json](qa-002-evidence.json) และ [qa-002-results.md](qa-002-results.md) "
        "ไม่มีการปรับ Expected หลังเห็นผล: ถ้าระบบไม่ตรงกับ Expected จะบันทึกเป็นข้อบกพร่องใน [qa-002-defects.md](qa-002-defects.md)",
        "",
        "## วิธีทดสอบและสภาพแวดล้อม",
        "",
        "| หัวข้อ | รายละเอียด |",
        "| --- | --- |",
        "| วิธี | `WebApplicationFactory<Program>` เรียก pipeline จริงทั้งหมด (middleware, cookie, session, antiforgery, controller) ผ่าน HTTPS จำลอง ใน `BC-LocalAI.Tests/Qa002/` สั่งรันและรวมหลักฐานด้วย `tests/security/run_qa002.py` |",
        "| ทำไมไม่ black-box ผ่านโปรเซสจริง | เส้นทาง login และตรวจบัญชีทุก request ใช้ MySQL เท่านั้น (ไม่มีทางเลือกที่ไม่ใช้ MySQL ใน Production code และห้ามเพิ่ม) จึงใช้ host ทดสอบแทนส่วนฐานข้อมูล: ที่เก็บบัญชีจำลอง (`FakeUserStore`) และตัวนับความพยายาม login จำลอง |",
        "| Ollama | จำลองด้วย `HttpMessageHandler` ที่นับคำขอ (ตัวนับ Ollama) ใช้ `OllamaAIService` และ `OllamaStreamingService` จริงเพื่อให้ข้อความผิดพลาดเป็นของจริง ไม่ต่อ Ollama จริง (พอร์ต 11434) |",
        "| บัญชี | สังเคราะห์ทั้งหมด (alice, bob, admin1 ฯลฯ) รหัสผ่านสุ่มใหม่ทุกครั้งที่รัน ไม่มีบัญชีหรือรหัสผ่านจริง |",
        "| เวลา | นาฬิกาที่ปรับด้วยมือ (`ManualClock`) เพื่อทดสอบ idle 30 นาที / absolute 8 ชั่วโมง / lockout 15 นาที โดยไม่ต้องรอจริง |",
        "| รอบ | ทุกกรณีรัน 2 รอบเต็ม (สั่งแยกกัน) ผลรวมในหลักฐาน |",
        "",
        "สัญลักษณ์ role ในตาราง M: `anonymous` ไม่ล็อกอิน · `user` User · `admin` Admin · `logged-out` cookie ของ session ที่ออกจากระบบแล้ว · "
        "`disabled` บัญชีถูกปิดระหว่างล็อกอิน · `idle-expired` ไม่ใช้งาน 30 นาที · `absolute-expired` ครบ 8 ชั่วโมง · `role-changed` เปลี่ยน role ระหว่างล็อกอิน",
        "",
    ]
    for group, title in data["groups"].items():
        rows = [c for c in cases if c["group"] == group]
        if not rows:
            continue
        out += [f"## {group}. {title} ({len(rows)} กรณี)", "", "| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |", "| --- | --- | --- | ---: | --- | ---: | --- |"]
        for c in rows:
            out.append(f"| {cell(c['id'])} | {cell(c['title'])} | {cell(c['expected'])} | {cell(c['status'])} | {cell(c['code'])} | {cell(c['ollama'])} | {cell(c['basis'])} |")
        out.append("")
    out += [
        "คอลัมน์ HTTP/Code/Ollama: `-` คือไม่ได้กำหนดค่านั้นเป็นเกณฑ์ (ตัดสินด้วยข้อความ Expected) · Ollama คือจำนวนคำขอที่ fake Ollama ต้องได้รับ",
        "",
        f"รวม {len(cases)} กรณี",
        "",
    ]
    return "\n".join(out)


def results_table() -> str:
    data = load_catalog()
    evidence = json.loads(EVIDENCE.read_text(encoding="utf-8"))
    per_case = {c["id"]: c for c in evidence["cases"]}
    out = [
        "# QA-002 ผลรายกรณี: Expected เทียบ Actual (D3)",
        "",
        f"สร้างอัตโนมัติจาก [qa-002-evidence.json](qa-002-evidence.json) · รัน {evidence['runs']} รอบ · Expected มาจาก [qa-002-test-matrix.md](qa-002-test-matrix.md) (ไม่แก้หลังเห็นผล) · "
        "ผล `ผ่าน` = Actual ตรงกับ Expected ทุกรอบ, `ไม่ผ่าน` = มีรอบที่ไม่ตรง (ดูข้อบกพร่อง), `ไม่ได้รัน` = ไม่มีหลักฐาน",
        "",
        "| ID | HTTP expected → actual | Code expected → actual | Ollama expected → actual | ผล | รอบที่ผ่าน | ข้อบกพร่อง |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for c in data["cases"]:
        e = per_case.get(c["id"])
        if e is None:
            out.append(f"| {cell(c['id'])} | {cell(c['status'])} → - | {cell(c['code'])} → - | {cell(c['ollama'])} → - | ไม่ได้รัน | 0/{evidence['runs']} | - |")
            continue
        verdict = {"PASS": "ผ่าน", "FAIL": "ไม่ผ่าน", "OBSERVED": "สังเกต"}.get(e["verdict"], e["verdict"])
        out.append(
            f"| {cell(c['id'])} | {cell(c['status'])} → {cell(e['actualStatus'])} | {cell(c['code'])} → {cell(e['actualCode'])} | "
            f"{cell(c['ollama'])} → {cell(e['actualOllama'])} | {verdict} | {e['passedRuns']}/{e['runs']} | {cell(e.get('defect'))} |"
        )
    out.append("")
    return "\n".join(out)


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if not argv or argv[0] not in ("matrix", "results"):
        print(__doc__)
        return 2
    if argv[0] == "matrix":
        MATRIX_MD.write_text(matrix(), encoding="utf-8", newline="\n")
        print(f"wrote {MATRIX_MD.relative_to(ROOT)}")
    else:
        TABLE_MD.write_text(results_table(), encoding="utf-8", newline="\n")
        print(f"wrote {TABLE_MD.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
