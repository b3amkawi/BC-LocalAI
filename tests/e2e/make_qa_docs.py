#!/usr/bin/env python3
"""Builds docs/qa/qa-001-test-plan.md and docs/qa/qa-001-results.md from the evidence files.

The tables are generated so the numbers in the reports always match the evidence JSON:
    python tests/e2e/make_qa_docs.py
"""
import json
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
QA = ROOT / "docs" / "qa"
GROUPS = {"3.1": "3.1 สถานการณ์ปกติ", "3.2": "3.2 สถานการณ์ผิดพลาด", "3.3": "3.3 การยกเลิก",
          "3.4": "3.4 ความปลอดภัยของการแสดงผล", "3.5": "3.5 หลายคำขอพร้อมกัน", "regression": "Regression ของ POST /api/chat"}


def load(name):
    return json.loads((QA / name).read_text(encoding="utf-8"))


def esc(text):
    return str(text).replace("|", "\\|").replace("\n", " ")


def plan():
    cases = load("../../tests/e2e/streaming-cases.json")
    mock = load("qa-001-evidence.json")
    live = load("qa-001-live-evidence.json")
    lines = ["# QA-001 Test Plan and Case List (D1)", "",
             "แผนทดสอบเส้นทาง streaming ตั้งแต่ผู้ใช้ถึง Ollama: SSE client → `POST /api/chat/stream` (Backend ที่ build จริง ใช้ Production) → Ollama",
             "Expected ของทุกกรณีถูกเขียนก่อนรัน (ในโค้ดของชุดทดสอบ) และ Actual บันทึกแยกใน evidence ไม่ปรับ Expected หลังเห็นผลโดยไม่บันทึก", "",
             "## ขอบเขตและวิธีทดสอบ", "",
             "| ชุด | วิธี | ใช้ทดสอบอะไร | ไฟล์ |", "| --- | --- | --- | --- |",
             f"| Mock | Backend จริงต่อกับ Ollama จำลองบน loopback ที่สั่งพฤติกรรมได้ | ความผิดพลาดที่ทำซ้ำบนโมเดลจริงยาก: runner ล้ม, หยุดนิ่ง, record เสีย, ข้อมูลเกินขนาด, คิว | `tests/e2e/streaming_e2e.py` ({mock['cases']} กรณี รันกรณีละ {mock['repeats']} ครั้ง) |",
             f"| Live | Backend จริงต่อกับ Ollama และ qwen3:4b จริง | สถานการณ์ปกติ, cold start, การยกเลิก, หลายคำขอ, `/api/chat` เดิม ตรวจเชิงโครงสร้างเพราะคำตอบโมเดลไม่คงที่ | `tests/e2e/live_streaming_e2e.py` ({live['cases']} กรณี รันกรณีละ {live['repeats']} ครั้ง) |",
             "| UI | เบราว์เซอร์กับ FE-002 | ปุ่ม Stop, ปิดแท็บ, การแสดง HTML, สถานะรอคิว | **ยังทดสอบไม่ได้: FE-002 ยังไม่ merge** |", "",
             "ทุกกรณีรันอย่างน้อย 2 ครั้ง และจำนวนกรณีเป็น 2 เท่าของรายการตัวอย่างใน Issue (แต่ละหัวข้อมีตัวแปรภาษาไทย/อังกฤษหรือรูปแบบสองแบบ)", "",
             "กติการ่วม: ไม่มีข้อมูลส่วนตัว รหัสผ่าน หรือ token ในชุดคำถาม ใช้ canary string ตรวจว่า log ของ Backend ไม่มีข้อความผู้ใช้/คำตอบ ข้อบกพร่องด้านความปลอดภัยถือเป็น blocker", ""]
    by = {}
    for c in cases["cases"]:
        by.setdefault(c["group"], []).append(c)
    lines += ["## กรณีทดสอบชุด Mock", ""]
    for g in ("3.1", "3.2", "3.3", "3.4", "3.5", "regression"):
        lines += [f"### {GROUPS[g]}", "", "| ID | กรณี | เจ้าของ | Expected |", "| --- | --- | --- | --- |"]
        for c in by.get(g, []):
            lines.append(f"| {c['id']} | {esc(c['title'])} | {c['owner']} | {esc(c['expected'])} |")
        lines.append("")
    lines += ["### log ของ Backend", "", "| ID | กรณี | เจ้าของ | Expected |", "| --- | --- | --- | --- |"]
    for r in mock["results"]:
        if r["id"] == "QA1-L-log-privacy":
            lines.append(f"| {r['id']} | {esc(r['title'])} | {r['owner']} | {esc(r['expected'])} |")
    lines += ["", "## กรณีทดสอบชุด Live", "", "| ID | หัวข้อ | กรณี | Expected |", "| --- | --- | --- | --- |"]
    for r in live["results"]:
        lines.append(f"| {r['id']} | {r['group']} | {esc(r['title'])} | {esc(r['expected'])} |")
    lines += ["", "## กรณีที่ยังทดสอบไม่ได้ (รอ FE-002)", "", "| ID | หัวข้อ | กรณี | เจ้าของ | รอ |", "| --- | --- | --- | --- | --- |"]
    for b in cases["blocked"]:
        lines.append(f"| {b['id']} | {b['group']} | {b['title']} | {b['owner']} | {b['waiting_for']} |")
    lines += ["", "Expected ของกรณี UI: Stop หยุดการสร้างและข้อความสถานะเป็น `cancelled`; ปิดแท็บ/เปลี่ยนหน้าทำให้ Ollama หยุดภายใน 1.5 วินาที; คำตอบที่มี `<script>` แสดงเป็นข้อความ; มีตัวบอกสถานะรอคิวเมื่อมีหลายคำขอ; ข้อความบางส่วนที่รับก่อน error แสดงพร้อมข้อความ error", "",
              "## วิธีรัน", "", "```bash", "dotnet build BC-LocalAI.slnx", "python tests/e2e/streaming_e2e.py --output docs/qa/qa-001-evidence.json", "python tests/e2e/live_streaming_e2e.py --output docs/qa/qa-001-live-evidence.json   # ต้องมี Ollama + qwen3:4b", "python tests/e2e/make_qa_docs.py", "```", ""]
    (QA / "qa-001-test-plan.md").write_text("\n".join(lines), encoding="utf-8")


def mark(r):
    return "ผ่าน" if r["passed"] else "**ไม่ผ่าน**"


def results():
    mock = load("qa-001-evidence.json")
    live = load("qa-001-live-evidence.json")
    initial = load("qa-001-evidence-initial.json")
    L = ["# QA-001 Results and Verification Report (D3, D5)", "",
         f"ทดสอบเมื่อ {mock['generated_utc']} UTC (Mock) และ {live['generated_utc']} UTC (Live) บน Windows 11, RTX 4050 Laptop 6 GB, Ollama {live['ollama_version']}, qwen3:4b",
         "Backend: build จาก `main` (รวม BE-003 และ BE-004), `ASPNETCORE_ENVIRONMENT=Production`, ชุดทดสอบอยู่ใน `tests/e2e/`", "",
         "## สรุป", "",
         "| ชุด | กรณี | รัน | ผ่าน | ไม่ผ่าน | ใช้เวลา |", "| --- | ---: | ---: | ---: | ---: | ---: |",
         f"| Mock | {mock['cases']} | {mock['case_runs']} | {mock['passed_cases']} | {len(mock['failed_cases'])} | {mock['duration_s']:.0f} วินาที |",
         f"| Live | {live['cases']} | {live['case_runs']} | {live['passed_cases']} | {len(live['failed_cases'])} | {live['duration_s']:.0f} วินาที |",
         f"| UI (FE-002) | {len(mock['blocked'])} | 0 | - | - | **ยังไม่ได้ทดสอบ** |", "",
         "**ไม่พบข้อบกพร่องด้านความปลอดภัย** และไม่มีเศษ JSON, ข้อความคิดของโมเดล หรือรายละเอียดภายในของ Ollama (พอร์ต, ค่าที่คล้าย secret) ปรากฏแก่ผู้ใช้ในทุกกรณี Backend ไม่ส่ง `completed` เมื่อสตรีมไม่สมบูรณ์ในทุกกรณีผิดพลาด และ log ของ Backend ไม่มีคำถามหรือคำตอบ",
         "ข้อสังเกตที่ควรตัดสินใจอยู่ใน [qa-001-defects.md](qa-001-defects.md)", "",
         "## การบันทึกการแก้ Expected และความผิดพลาดของชุดทดสอบเอง", "",
         f"รอบแรกที่ใช้ชุดทดสอบที่ใช้งานได้จริง ผ่าน {initial['passed_cases']}/{initial['cases']} กรณี (เก็บไว้ที่ `qa-001-evidence-initial.json`) ที่ไม่ผ่านทั้งหมดเป็นความเข้าใจผิดของผู้เขียนชุดทดสอบ ไม่ใช่ข้อบกพร่องของ Backend:", "",
         "| กรณีที่ไม่ผ่านในรอบแรก | Expected เดิม | ที่พบ | สาเหตุและการแก้ |", "| --- | --- | --- | --- |",
         "| runner ล้ม / สตรีมจบไม่มี done / record error / หยุดนิ่งกลางสตรีม (อย่างละ 2 กรณี) | `partial=true` | `partial=false` | Backend เก็บท้ายคำตอบสั้น ๆ ไว้ตรวจ `<think>` ที่ถูกแบ่งข้าม record จึงยังไม่ส่ง delta เมื่อมีข้อความน้อยกว่าราว 15 ตัวอักษร `partial` จึงเป็น false ถูกต้อง แยกเป็นสองกรณีต่อชนิด: ข้อความน้อย (`partial=false`) และข้อความมากพอ (`partial=true`) ทั้งสองผ่าน |",
         "| คิวเต็ม | คำขอที่สองได้ `AI_TIMEOUT` | ได้ `completed` | ข้อผิดพลาดของ Ollama จำลองในชุดทดสอบ: คำขอที่สองไม่ได้เข้าคิวแบบ serial แก้ให้เข้าคิวและให้ส่ง header หลังได้คิวเหมือน Ollama จริง |",
         "| ก่อนรอบนี้ | - | - | ชุดทดสอบอ่านตัว request แบบ chunked ของ Backend ไม่ได้ ทุกกรณีจึงตอบคำตอบตั้งต้น แก้แล้วก่อนนับผล รอบนั้นไม่ใช่ผลของระบบ |", ""]
    for title, data, idp in (("ชุด Mock", mock, "QA1"), ("ชุด Live", live, "L")):
        L += [f"## ผลรายกรณี: {title}", "", "| ID | หัวข้อ | กรณี | ผล | รอบที่ผ่าน |", "| --- | --- | --- | --- | --- |"]
        for r in data["results"]:
            ok = sum(1 for x in r["runs"] if x["passed"])
            L.append(f"| {r['id']} | {r['group']} | {esc(r['title'])} | {mark(r)} | {ok}/{len(r['runs'])} |")
        L.append("")
    # timings
    cancel = [x["actual"] for r in mock["results"] if r["group"] == "3.3" for x in r["runs"] if x["actual"].get("next_first_delta_s") is not None]
    detect = [a["disconnect_detected_s"] for a in cancel if a.get("disconnect_detected_s") is not None]
    nxt = [a["next_first_delta_s"] for a in cancel]
    live_c = [x["actual"]["next_first_delta_s"] for r in live["results"] if r["group"] == "3.3" for x in r["runs"] if x["actual"].get("next_first_delta_s") is not None]
    L += ["## เวลาที่วัดได้", "",
          "| รายการ | n | ต่ำสุด | กลาง | สูงสุด |", "| --- | ---: | ---: | ---: | ---: |"]
    def row(name, v):
        return f"| {name} | {len(v)} | {min(v):.2f} s | {statistics.median(v):.2f} s | {max(v):.2f} s |" if v else f"| {name} | 0 | - | - | - |"
    L += [row("Mock: Ollama จำลองรู้ว่า client ปิดหลังการยกเลิก", detect), row("Mock: delta แรกของคำขอถัดไปหลังยกเลิก", nxt), row("Live (โมเดลจริง): delta แรกของคำขอถัดไปหลังยกเลิก", live_c), ""]
    for label, data in (("Mock", mock), ("Live", live)):
        walls = {}
        for r in data["results"]:
            if r["group"] == "3.5":
                for x in r["runs"]:
                    a = x["actual"]
                    if a.get("requests"):
                        walls.setdefault(a["requests"], []).append(a["wall_s"])
        L += [f"**หลายคำขอพร้อมกัน ({label})** เวลารวมที่ทุกคำขอเสร็จ (Ollama ทำทีละคำขอ จึงเพิ่มตามจำนวนคำขอ):", "",
              "| คำขอพร้อมกัน | n | เวลารวมต่ำสุด | สูงสุด |", "| ---: | ---: | ---: | ---: |"]
        for n in sorted(walls):
            L.append(f"| {n} | {len(walls[n])} | {min(walls[n]):.1f} s | {max(walls[n]):.1f} s |")
        L.append("")
    L += ["## สิ่งที่ยังไม่ได้ทดสอบและข้อจำกัด", "",
          "- กรณี UI ทั้ง 6 ข้อ (รอ FE-002 merge): ปุ่ม Stop, ปิดแท็บ, เปลี่ยนหน้า, การแสดง HTML ในหน้าแชต, ตัวบอกสถานะรอคิว, การแสดงข้อความบางส่วนก่อน error",
          "- timeout ที่ค่าเริ่มต้น 180 วินาทีไม่ได้รอจริง ทดสอบที่ 3 วินาที (instance แยกที่ตั้ง `Ollama__TimeoutSeconds=3`) พฤติกรรมเหมือนกันแต่เวลาต่างกัน",
          "- การ inject ความผิดพลาดกับ Ollama จริง (runner ล้ม, ปิดทั้งตัว) อยู่ใน AI-007 (#28) ไม่ซ้ำที่นี่",
          "- reverse proxy (การ buffer ของ SSE) และเบราว์เซอร์นอกขอบเขต ไม่ได้ทดสอบ",
          "- ไม่มีการทดสอบโหลดเกิน 5 คำขอพร้อมกัน (นอกขอบเขต Issue) และไม่ทดสอบ authentication (QA-002)",
          "- ชุด Live ตรวจเชิงโครงสร้าง ไม่ตัดสินความถูกต้องของเนื้อหาคำตอบ (อยู่ใน AI-003)", ""]
    (QA / "qa-001-results.md").write_text("\n".join(L), encoding="utf-8")


if __name__ == "__main__":
    plan()
    results()
    print("written:", QA / "qa-001-test-plan.md", QA / "qa-001-results.md")
