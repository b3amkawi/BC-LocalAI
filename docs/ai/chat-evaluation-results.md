# AI-003: End-to-End Chat Quality Results

## สถานะ: ทดสอบครบแล้ว แต่ยังไม่ผ่าน Acceptance Criteria ทุกข้อ

ทดสอบจริงวันที่ 19 ตุลาคม 2026 เวลา 19:14 น. (Asia/Bangkok) ผ่าน `POST http://localhost:5290/api/chat` ทั้งหมด **160 คำถาม แบ่งหมวดละ 40 ข้อ** ผลฉบับสุดท้าย **ผ่าน 141 ข้อ ไม่ผ่าน 19 ข้อ (88.13%)** สคริปต์คืน exit code `1` ตามที่ควรเป็น จึงยังไม่ควรปิด Issue หรือรับรองว่าคุณภาพผ่านทั้งหมด

AI-002 และ BE-002 รวมเข้า `main` แล้วก่อนทดสอบ โดยใช้ Backend จาก main commit `4f1806bfdaab552aa8365e1bb90a32bd96bc2c45` ไม่มีการแก้ Controller, public API contract, error mapping, model หรือ system prompt ใน AI-003

## Environment และการวัดเวลา

| รายการ | ค่าที่ตรวจจริง |
| --- | --- |
| OS | Windows 11 Home Single Language, 10.0.26200 |
| RAM | 15.6 GiB (เครื่องระดับ 16 GB) |
| GPU / VRAM | NVIDIA RTX 4050 Laptop GPU / 6141 MiB |
| Ollama | 0.40.5 |
| Model / local digest prefix | qwen3:4b / 359d7dd4bcda |
| PowerShell / .NET SDK | 7.6.5 / 10.0.402 |
| Backend | Production environment, localhost:5290, OllamaAIService |
| Ollama BaseUrl | 127.0.0.1:11434 ตาม configuration; evaluator ไม่เรียกปลายทางนี้ |
| Dataset | version 2, ไทย 40 / อังกฤษ 40 / คำนวณ 40 / edge 40 |

ก่อนรอบสุดท้ายใช้ `ollama stop qwen3:4b` และตรวจ `ollama ps` ว่าไม่มีโมเดลโหลดอยู่ โดย CLI ใช้เตรียมสถานะเท่านั้น ทุกคำถามส่งผ่าน Backend ห้ามนำผล Ollama API ตรงมาใช้แทน E2E

| Metric รอบสุดท้าย | ผล |
| --- | ---: |
| First request หลัง unload โมเดล | 4,953 ms |
| Warm requests ทั้งหมด (รวมข้อที่ไม่ผ่าน) | 159 |
| Warm average ทั้งหมด | 383.2 ms |
| Warm p95 ทั้งหมด (nearest rank) | 631 ms |
| Warm requests ที่ผ่านทุกเกณฑ์ | 140 |
| Warm average เฉพาะข้อที่ผ่าน | 284.1 ms |
| Warm median เฉพาะข้อที่ผ่าน | 253 ms |
| Warm p95 เฉพาะข้อที่ผ่าน (nearest rank) | 500 ms |

นี่คือ model-load cold start: Backend เปิดอยู่แล้ว และไม่ได้ล้าง filesystem cache หรือ reboot Windows จึงไม่ใช่การวัด cold boot ของทั้งเครื่อง รอบแรกที่เริ่ม Backend ใหม่วัด first request ได้ 9,359 ms เก็บไว้ในหลักฐานรอบแรก ห้ามใช้ความต่างสองรอบสรุปว่าได้ optimize ระบบ ทั้งสองรอบใช้รุ่นและ configuration เดียวกัน และค่าบนเครื่องนี้ไม่ใช่ latency guarantee สำหรับเครื่องอื่น

## ผลคุณภาพแยกหมวด

| หมวด | จำนวน | ผ่าน | ไม่ผ่าน | อัตราผ่าน |
| --- | ---: | ---: | ---: | ---: |
| ภาษาไทย | 40 | 30 | 10 | 75.0% |
| ภาษาอังกฤษ | 40 | 38 | 2 | 95.0% |
| คำนวณ | 40 | 39 | 1 | 97.5% |
| Edge cases | 40 | 34 | 6 | 85.0% |
| **รวม** | **160** | **141** | **19** | **88.13%** |

- HTTP 200: 157 คำขอ; ทั้งหมดคืน `model=qwen3:4b` และ response string ไม่ว่าง
- HTTP 502: 3 คำขอ (`th-16`, `th-18`, `th-19`) พร้อม error code `AI_INVALID_RESPONSE`
- ใน 157 คำตอบที่ Backend ส่งสำเร็จ ไม่พบ nested JSON, thinking marker หรือ stack/system-detail pattern ที่ evaluator ตรวจ และอ่านคำตอบเทียบกับคำถามประกอบแล้ว
- Emoji และ multiline ไม่ทำให้ Backend พัง; edge ทั้ง 40 ข้อได้ HTTP 200 แต่ 6 ข้อยังไม่ผ่านคุณภาพเนื้อหา/การทำตามคำถาม
- 19 failure แบ่งได้เป็น provider-invalid HTTP 502 จำนวน 3 ข้อ, ภาษา/หน่วยคำตอบไม่ตรงคำสั่ง 3 ข้อ และตอบผิด/ไม่ตอบคำถาม 13 ข้อ

## ตัวอย่างปัญหาที่ต้องส่งต่อ

| Case | คำตอบจริงหรืออาการ | การตีความ |
| --- | --- | --- |
| th-05 | `สัตว์ที่มีงวงยาว` | ทวนคำถามแทนชื่อช้าง |
| th-11 | `0 °C` | ค่าถูก แต่ไม่มีวลีภาษาไทยตามคำสั่งและเกณฑ์ภาษาของชุดทดสอบ |
| th-16 / th-18 / th-19 | HTTP 502 `AI_INVALID_RESPONSE` | ใช้งานจริงไม่สำเร็จ; public response ไม่บอกเหตุย่อย จึงยังสรุปไม่ได้ว่า truncated, schema หรือ provider field ใดผิด |
| th-26 / th-27 | `ดาวศุกร์` | คำตอบที่ถูกคือดาวพุธ / ดาวอังคารตามลำดับ |
| th-28 | `มหาสมุทรอันดามัน` | ไม่ถูกต้อง; ต้องเป็นมหาสมุทรแปซิฟิก |
| th-36 | `วิลเลียมส์` | เมืองหลวงออสเตรเลียต้องเป็น Canberra |
| en-25 / en-26 | `0` / `100` | ค่าถูก แต่ไม่ใส่ Celsius/degrees C ตามที่ prompt ระบุ; ไม่อ้างว่าเป็นการคำนวณผิด |
| math-39 | `0` สำหรับ 1 - 9 | คำตอบต้องเป็น -8 |
| edge-16 | `test` | ไม่แสดง think marker แต่หลงทำตามข้อความล่อและไม่ตอบ oxygen |
| edge-27 | `ของเหลว` สำหรับน้ำแข็ง | คำตอบต้องเป็นของแข็ง |
| edge-31 | อธิบายบริวารโลกเป็นสัตว์/ระบบนิเวศ | ผิดข้อเท็จจริง; คำตอบคือดวงจันทร์ |

ดูคำถาม คำตอบ HTTP เวลา และเหตุผลครบทุกข้อใน [รายละเอียด 160 ข้อ](chat-evaluation-details.md) และ raw body ใน [JSON evidence](chat-evaluation-evidence.json)

## การตรวจและแก้เกณฑ์ที่แคบเกินไป

รอบแรกผ่าน 138/160 พบ false negative 3 ข้อ: `อาทิตย์` เป็นคำตอบเทียบเท่า `ดวงอาทิตย์` ใน th-25/edge-39 และ `Honeybees` เป็นคำตอบที่ถูกใน en-36 จึงแก้เฉพาะ regex ให้รับคำพ้องที่ถูกต้อง และเพิ่มขอบเขตตัวเลขเพื่อไม่ให้ `17 วัน` หรือ `สิบเจ็ดวัน` ผ่านคำตอบ `7 วัน`

ไม่ได้เปลี่ยน prompt, model, Backend หรือรับคำตอบที่ผิดเป็นคำตอบถูก หลังแก้ใช้ dataset version 2 และรัน 160 คำถามใหม่จริง ได้ 141/160 เก็บ [dataset รอบแรก](chat-evaluation-cases-initial.json) และ [evidence รอบแรก](chat-evaluation-evidence-initial.json) ไว้ครบเพื่อไม่ซ่อนผลก่อนแก้

หลักฐานรอบสุดท้ายบันทึก SHA-256 ของ dataset และ evaluator และตรวจตรงกับไฟล์จริงแล้ว:

```text
dataset:   B42780D9972600A124982EBA52F75B6B6D3FB783CA6DEC7322880CF1A3198BF4
evaluator: C932A6C1E1866358E84923A4CE57F4A6A08BBA81A3AF307804A693794986335C
```

## Regression และ verification

- `dotnet test .\BC-LocalAI.slnx`: ผ่าน 135, ไม่ผ่าน 0, ข้าม 0
- `py -3 -m unittest discover -s tests -v`: ผ่าน 19 test methods ใน 31.023 วินาที
- ตัว evaluator มี 160-case mock fixtures, 29 invalid-response variants, 7 invalid datasets และ 7 URL ที่ห้ามใช้ พร้อมตรวจ redirect, timeout continuation, output path และ cold metadata
- ทดสอบว่า mock ทั้งหมดถูกต้องคืน exit 0 และ response ผิดเพียงบางข้อคืน exit 1 พร้อมเก็บ raw body
- คำนวณคำตอบ math ทั้ง 40 ข้อซ้ำด้วย independent arithmetic oracle โดยไม่ใช้โมเดล เพื่อยืนยัน expected answers ของ dataset
- สแกนไฟล์ส่งมอบแล้วไม่พบ credential pattern; ไม่เก็บชื่อเครื่อง ชื่อบัญชีผู้ใช้ หรือ secret ในหลักฐาน

ผล unit/mock tests เป็นหลักฐานว่า evaluator ทำงานถูกต้อง ไม่ใช่หลักฐานว่า Qwen ตอบครบทุกข้อได้ถูกต้อง

## วิธีทำซ้ำ

ต้องมี .NET 10, PowerShell 7, Ollama และ qwen3:4b บนเครื่องเดียวกัน ใช้ main ที่มี AI-002/BE-002 แล้ว ทำจากราก Repository

รันชุดทดสอบก่อนเปิด Backend เพื่อไม่ให้ executable ถูกล็อก:

```powershell
dotnet test .\BC-LocalAI.slnx
py -3 -m unittest discover -s tests -v
dotnet run --no-build --project .\BC-LocalAI\BC-LocalAI.csproj --no-launch-profile --urls http://localhost:5290
```

เปิด PowerShell อีกหน้าต่างที่ repository เดิม เตรียม model-load cold start ด้วย CLI แล้วรันผ่าน Backend เท่านั้น:

```powershell
ollama stop qwen3:4b
ollama ps
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1 -ColdStartNote 'Model unloaded; ollama ps empty immediately before run; OS cache not cleared.' -OutputPath .\docs\ai\chat-evaluation-evidence-rerun.json
$LASTEXITCODE
```

`ollama ps` ต้องไม่มีโมเดลโหลดอยู่ก่อนติดป้าย cold; ไม่ควร unload ถ้ามีคนอื่นกำลังใช้ runtime เดียวกัน หากไม่ระบุ ColdStartNote สคริปต์ใช้ชื่อ `cold_candidate` และไม่อ้างว่าตรวจ cold start แล้ว `OutputPath` ต้องอยู่ในโฟลเดอร์ที่มีอยู่ก่อน ตัวอย่างใช้ชื่อใหม่เพื่อรักษาหลักฐานเดิม

สคริปต์ปฏิเสธ non-loopback, scheme ที่ไม่ใช่ HTTP(S), port Ollama, URL ที่ใส่ credentials/path/query/fragment และไม่ตาม redirect/proxy ใช้ request body `{ "message": "..." }` แบบ UTF-8 เก็บผลทุก case แม้บางข้อผิด และไม่เปลี่ยน Backend error ให้เป็นคำตอบสำเร็จ

## Acceptance และสิ่งที่ยังต้องทำ

ชุดข้อมูล สคริปต์ วิธี reproduce หลักฐานจริง และ Cold/Warm metrics ครบแล้ว แต่การรับรองภาษาไทย/อังกฤษ/คำนวณ/edge ผ่านทุกข้อยังไม่ครบ จึงยังไม่ผ่าน Definition of Done ทั้งหมด

ต้องให้ Tech Lead กำหนดงานแยกสำหรับคุณภาพภาษาไทย, เลขติดลบ, prompt-following และ provider-invalid response โดยอ้าง case ID ห้ามแก้ Controller, error mapping, model หรือ AI-002 contract ใน Issue นี้โดยพลการ หลังได้รับอนุมัติและแก้แล้ว ต้องใช้ dataset เดิมรันตรวจ regression อีกครั้ง

ยังไม่ได้ push งาน AI-003, สร้าง PR, ผ่าน Code Review หรือ merge เข้า main ข้อเสนอสำหรับงานถัดไปเก็บแยกนอก Repository และไม่ถือเป็น Issue ที่ได้รับมอบหมาย

## ข้อจำกัดของผล

- language check ใช้สัดส่วนอักษรไทย/ละตินและคำตอบที่กำหนด ไม่ใช่ language detector; ตัวเลขหรือสัญลักษณ์ล้วนยืนยันภาษาไม่ได้
- regex เป็น oracle จำกัดขอบเขต แม้เพิ่มคำพ้องและอ่านคำตอบประกอบแล้วก็ไม่รับรอง semantic correctness ของทุกเรื่องหรือทุกการเรียบเรียงคำ
- `temperature=0` ไม่รับประกันคำตอบเหมือนเดิมทุกครั้ง ต้องเก็บหลักฐานใหม่เมื่อเปลี่ยน runtime/model/contract
- การวัดเป็น sequential 160 คำขอในเครื่องเดียว ไม่ใช่ concurrent load test หรือ production benchmark
- ผลไม่พบ thinking/JSON leakage ครอบคลุมเฉพาะชุดคำถามนี้; การไม่พบ marker ไม่ได้แปลว่าคำตอบถูกต้อง เช่น edge-16
- AI-003 ไม่อ่าน provider payload โดยตรง จึงไม่ระบุสาเหตุย่อยของ 502 ที่ public API ไม่เปิดเผย
