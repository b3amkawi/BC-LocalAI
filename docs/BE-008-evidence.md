# BE-008 Evidence

วันที่: 2026-10-21 · Branch `watzapon_BE-008` จาก `origin/main` e6ab412 (baseline: `dotnet test` 1137 ผ่านก่อนแก้)

## 1. ผลทดสอบอัตโนมัติ (mock — พิสูจน์ logic ไม่ใช่ความจุจริง)

| คำสั่ง | ผล |
|---|---|
| `dotnet build BC-LocalAI.slnx` | สำเร็จ 0 warning |
| `dotnet test BC-LocalAI.slnx` (ชุดเต็ม) | **1185 ผ่าน / 0 ล้ม / 0 ข้าม** (1137 เดิม + 48 ใหม่) |
| `ChatAdmission*` ×3 รอบติดกัน | 48/48 ผ่านทุกรอบ (ไม่ flaky) |
| `scan_secrets.py`, `check_doc_links.py`, `tests.test_ci_scripts` | สะอาด / OK / OK |

ระหว่างทำพบว่า `Qa002StreamTests.OneUserFlooding_*` ล้มเมื่อเปิดเพดานต่อผู้ใช้ (สอดคล้องที่ BE-008 แก้ QA2-D06); แก้โดยคง baseline ของ case นั้นด้วย config (ดู checklist ข้อ 3) แล้ว Qa002 ทั้ง 187 ตัวผ่าน

### Expected เทียบ Actual (สรุป ทุกข้อกำหนด Expected ก่อนรัน)

| กรณี | Expected | Actual |
|---|---|---|
| จำนวนคำขอใน Ollama พร้อมกัน | ≤ `MaxConcurrentRequests` (300 งานสุ่มยกเลิก, 30 คำขอ HTTP) | ผ่าน (`MaxRunning` ≤ ค่าตั้ง) |
| ผู้ใช้เกินโควตา | 429 `AI_REQUEST_IN_PROGRESS` + `Retry-After`, คำขอแรกไม่เสียหาย | ผ่านทั้ง chat/stream |
| คิวเต็ม | 503 `AI_BUSY` + `Retry-After` ทันที; stream ได้ JSON ไม่ใช่ SSE | ผ่าน; โมเดลถูกเรียกเฉพาะคำขอที่รับแล้ว |
| รอคิวเกินเวลา | `AI_BUSY` ไม่ใช่ `AI_TIMEOUT`; stream ได้ `error` event หลัง `started`; โมเดลไม่ถูกเรียก | ผ่าน |
| ยกเลิกตอนรอคิว | ไม่ถึงโมเดล, ตำแหน่งว่างทันที | ผ่าน (`Calls` คงเดิม) |
| ยกเลิกตอนประมวลผล | cancellation ถึงโมเดล, คนรอถัดไปได้ slot | ผ่าน |
| provider error / dispose ซ้ำ / grant-cancel race ×200 | ไม่มี slot รั่ว (`Snapshot==(0,0)` และรับงานใหม่ได้) | ผ่าน |
| ไม่ล็อกอิน | 401 ก่อนคิว (admitted=0, rejected=0, โมเดล 0 ครั้ง) | ผ่าน |
| ปลอม `X-User-Id`/`userId` | นับเป็นผู้ใช้ที่ยืนยันแล้ว → 429; อีกผู้ใช้ไม่ถูกกระทบ | ผ่าน |
| auth ปิด | ใช้โควตาร่วมเดียว | ผ่าน |

## 2. Live กับ Ollama จริง (เครื่องพัฒนา RTX 4060 Laptop, `qwen3:4b`, คำถามสังเคราะห์สั้น ๆ, auth ปิด)

Config: `MaxConcurrentRequests=1, MaxQueueLength=4, MaxPendingPerUser=16, EmitQueueEvents=true` ยิง 12 คำขอพร้อมกัน (สลับ stream / non-stream) ด้วย `scripts/test-chat-queue.py`; ผลดิบ [run1](BE-008/live-queue-run1.json), [run2](BE-008/live-queue-run2.json) (ไม่มีคำถาม/คำตอบ)

| รอบ | รับ (200) | ปฏิเสธ 503 `AI_BUSY` | หมายเหตุ |
|---|---:|---:|---|
| 1 | 5 | 7 | ทั้งหมดมี `Retry-After: 10`; ปฏิเสธใน ≈ 0.06 s |
| 2 | 5 | 7 | stream ที่ต้องรอได้ `started→queued→processing→delta→completed`; ปฏิเสธ ≈ 0.03 s |

5 = 1 กำลังทำ + 4 ในคิว ตรงค่าตั้ง; log 4101 บันทึก 14 ครั้ง (`Reason=queue-full Active=1 Queued=4`) ตรงกับ 14 การปฏิเสธ ไม่มีเนื้อหาสนทนา ลำดับว่าคำขอไหนได้รับเป็นผลของการแข่งกันตอนมาถึง

**ไม่ได้ทดสอบ live:** รอคิวเกิน 30 s, ยกเลิกกลางคิวกับ Ollama จริง, โควตาผู้ใช้จริงหลังล็อกอิน, ผู้ใช้จำนวนมาก (พิสูจน์ด้วย mock เท่านั้น) ตัวเลขความจุยังเป็นข้อเสนอจนกว่า AI-012 วัดบนเครื่องเป้าหมาย

## 3. ข้อจำกัดและความเสี่ยงที่เหลือ

ดู [BE-008-CONFIGURATION.md ข้อ 5, 7](BE-008-CONFIGURATION.md) และ [REVIEW-CHECKLIST ข้อ 4](BE-008-REVIEW-CHECKLIST.md) — สรุป: contract/ตัวเลขรออนุมัติ, คิวใน memory โปรเซสเดียว, ไม่มีเวลารอโดยประมาณ, ยังไม่ commit/push/PR

## 4. ทำซ้ำ

```powershell
dotnet test BC-LocalAI.slnx
$env:ChatQueue__MaxPendingPerUser="16"; $env:ChatQueue__MaxQueueLength="4"; dotnet run --project BC-LocalAI --urls http://127.0.0.1:5290
python -I -X utf8 scripts/test-chat-queue.py --base-url http://127.0.0.1:5290 --requests 12 --output run.json
```
