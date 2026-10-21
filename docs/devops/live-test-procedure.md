# ขั้นตอนรันชุด live (DEVOPS-001 D3)

ชุด live คือการทดสอบที่ต้องมี Ollama และ GPU จริง CI รันไม่ได้ จึงไม่ตรวจให้อัตโนมัติ
ผู้เขียน PR ที่แตะงาน AI ต้องรันเองบนเครื่องที่มี Ollama แล้วแนบหลักฐานใน PR ตามนี้

## เมื่อไรต้องรัน
PR ที่แก้สิ่งใดต่อไปนี้: prompt หรือ system prompt, schema ของ `format`, ชุดคำถาม (`docs/ai/*.json`), สคริปต์ใน `scripts/` ที่เรียก Ollama, ค่า `num_predict`/context, หรือ `OllamaAIService` และ `OllamaStreamingService`

## ชุด live ที่มี

| ชุด | คำสั่ง (PowerShell 7) | ผลที่ได้ |
| --- | --- | --- |
| AI-003 แชต 160 ข้อผ่าน `/api/chat` | `pwsh -File scripts/test-chat-e2e.ps1` (ต้องเปิด Backend ที่ `localhost:5290`) | `docs/ai/chat-evaluation-evidence.json` |
| AI-005 สตรีมกับ Ollama | `pwsh -File scripts/test-ollama-streaming.ps1` | `docs/ai/ollama-streaming-evidence.json` |
| AI-004 benchmark | ดู `docs/performance/benchmark-plan.md` | `docs/performance/benchmark-results*.csv` |
| BE-004 สตรีมของ Backend | `python scripts/test-chat-stream.py` | ตามเอกสาร BE-004 |

ใช้เครื่องที่ Ollama ฟัง loopback เท่านั้น ห้ามเปิด Ollama ออกนอกเครื่องเพื่อรันชุดนี้

## แนบหลักฐานใน PR

1. รันชุดที่เกี่ยวข้องจนผ่าน และ commit ไฟล์ evidence ที่สคริปต์สร้าง
2. สร้างบล็อกหลักฐาน:

   ```powershell
   pwsh -File scripts/ci/new-live-evidence.ps1 -Files docs/ai/chat-evaluation-cases.json,scripts/test-chat-e2e.ps1,docs/ai/chat-evaluation-evidence.json
   ```

   สคริปต์อ่านอย่างเดียว ไม่ส่งข้อมูลออก และถาม Ollama เฉพาะ loopback ผลลัพธ์ประกอบด้วยวันเวลา, OS, GPU, เวอร์ชัน Ollama, digest ของโมเดล และ SHA-256 ของไฟล์ที่ระบุ
3. วางบล็อกนั้นในช่อง "If this PR touches AI work" ของ PR template
4. ผู้รีวิวตรวจว่า SHA-256 ของ dataset และสคริปต์ตรงกับไฟล์ใน PR และ digest ของโมเดลตรงกับที่โครงการใช้ (`qwen3:4b`, digest `359d7dd4bcda`)

## ถ้าไม่มีเครื่องที่มี Ollama
ระบุใน PR ว่าไม่ได้รันชุด live พร้อมเหตุผล ผู้รีวิวตัดสินว่าจะ merge หรือรอผู้ที่มีเครื่อง ห้ามเขียนว่า "ผ่าน" โดยไม่ได้รัน
