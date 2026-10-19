# AI-003: End-to-End Chat Quality Results

## Current status: BLOCKED — live E2E not run

ยังไม่สามารถอ้างผล End-to-End จริงได้ เพราะ dependency ของ Issue ยังไม่ครบ ณ วันที่ 19 ตุลาคม 2026:

- AI-002 อยู่ใน PR #8 และยังไม่ merge เข้า `main`
- `main` ปัจจุบันลงทะเบียน `MockAIService` ซึ่งตอบ model `mock`
- BE-002 (`OllamaAIService`) ยังไม่ merge เข้า `main`

การรันตอนนี้จะทดสอบ Mock Backend และล้มเหลวเงื่อนไข `model=qwen3:4b` จึงไม่ใช่หลักฐานของ AI-003 ห้ามเปลี่ยน expected model เป็น `mock` เพื่อทำให้ผลผ่าน

## Prepared test coverage

[Dataset](chat-evaluation-cases.json) มี 30 cases และไม่มีข้อมูลส่วนตัว, password, token หรือ secret:

| Category | จำนวน |
| --- | ---: |
| ภาษาไทย | 8 |
| ภาษาอังกฤษ | 7 |
| คำนวณพื้นฐาน | 7 |
| Edge cases | 8 |
| **รวม** | **30** |

Edge cases ครอบคลุม Emoji ภาษาไทย/อังกฤษ, prompt หลายบรรทัด 2/3 บรรทัด, prompt injection ที่ขอ `<think>` และคำสั่งห้ามส่ง JSON/system detail

## Local script verification (not live E2E evidence)

รัน `py -3 -m unittest discover -s tests -v` แล้วผ่านทั้งหมด 11 tests ใน 20.778 วินาที ชุด AI-003 ใช้ mock Backend บน loopback เพื่อยืนยันว่า:

- สคริปต์ส่งทั้ง 20 mock cases ไปที่ `/api/chat` เท่านั้นและ body มีเฉพาะ `message`
- response ที่ถูก contract ทั้งหมดทำให้ script คืน exit code `0`
- response เพียงหนึ่งกรณีที่ model เป็น `mock` แทน `qwen3:4b` ทำให้ script คืน exit code `1` และ evidence ระบุ 19 ผ่าน/1 ไม่ผ่าน

ผลนี้ตรวจตัว evaluator เท่านั้น ไม่ใช้แทนการทดสอบ 30 cases ผ่าน Backend + Ollama จริง

## Validation performed by the script

`scripts/test-chat-e2e.ps1` เรียกเฉพาะ `POST /api/chat` ผ่าน absolute loopback BaseUrl และส่ง `{ "message": "..." }` ไม่เรียก port `11434` หรือ Ollama โดยตรง ตรวจทุกกรณีดังนี้:

- HTTP 200
- body เป็น JSON ตาม public Backend contract
- `response` เป็น string ที่ไม่ว่าง
- `model` ตรง `qwen3:4b`
- response ไม่ขึ้นต้นด้วย `{`
- ไม่มี `<think>` หรือ `</think>`
- ไม่มีรูปแบบ stack trace หรือรายละเอียดระบบที่กำหนด
- ภาษาไทย/อังกฤษตรงกับ dataset ด้วย heuristic
- factual/math answer ตรง regular expression ที่ตรวจสอบได้
- exit 0 เฉพาะเมื่อทุก case ผ่าน; กรณีใดล้มเหลวหรือบันทึก evidence ไม่ได้คืน exit 1

## How to run after dependencies merge

1. ดึง `main` ที่มี AI-002 และ BE-002 แล้ว
2. เปิด Ollama ผ่าน loopback และตรวจว่ามี `qwen3:4b`
3. เริ่ม Backend ที่ `http://localhost:5290`
4. เพื่อวัด cold start จริง ให้ unload/restart runtime ตามขั้นตอนที่ทีมอนุมัติก่อนเริ่ม script เพราะ script ห้ามเรียก Ollama โดยตรง
5. รันจากราก Repository:

```powershell
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1
$LASTEXITCODE
```

หลักฐานจะถูกเขียนที่ `docs/ai/chat-evaluation-evidence.json` คำขอแรกถูกระบุเป็น `cold_candidate`; อีก 29 คำขอเป็น `warm` พร้อมค่า average และ p95 ต้องตรวจว่าก่อนรันมีการ unload/restart จริง จึงจะเรียกคำขอแรกว่า cold start ได้

## Known limitations

- language check เป็น heuristic จากช่วงอักขระ ไม่ใช่ language detector เต็มรูปแบบ
- regular expression ตรวจคำตอบที่กำหนดได้ ไม่รับรองความถูกต้องของคำตอบทุกเรื่อง
- latency ขึ้นกับ model load, hardware, background load และ Ollama version ผลหนึ่งเครื่องไม่ใช่ performance guarantee
- test เป็น sequential quality evaluation ไม่ใช่ concurrent load test
- prompt injection cases ตรวจไม่ให้ marker/JSON หลุด แต่ไม่ใช่ security guarantee สำหรับ prompt ทุกแบบ

หลัง dependency พร้อม ต้องแทนสถานะ BLOCKED ด้วย environment จริง, Ollama version, hardware, commit, ผลผ่าน/ไม่ผ่านราย case, cold/warm measurements และข้อจำกัดที่พบ ห้ามสร้าง evidence ปลอมก่อนรัน
