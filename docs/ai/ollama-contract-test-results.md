# AI-002: ผลทดสอบ Ollama Integration Contract

ทดสอบวันที่ 19 ตุลาคม 2026 (Asia/Bangkok) บน Windows 11, Ollama `0.40.5`, `qwen3:4b`, PowerShell 7.6.5 และ NVIDIA RTX 4050 Laptop GPU 6141 MiB VRAM Reviewer ยืนยันแล้วว่าผล RTX 4050 ใช้รับ AI-002 ได้ เพราะ Issue นี้ตรวจ Integration Contract และไม่ได้วัดประสิทธิภาพ GPU

## Live API

ใช้ `scripts/test-ollama.ps1` เรียก `http://127.0.0.1:11434/api/generate` ผ่าน loopback เท่านั้น ทุกกรณีผ่าน HTTP 200, model `qwen3:4b`, boolean `done=true`, `done_reason=stop`, parse JSON ชั้นในได้, มี `answer` string ที่ไม่ว่างเพียง field เดียว และไม่มี think marker

| กลุ่ม | Prompt | Parsed answer | เวลา | หลักฐาน |
| --- | --- | --- | ---: | --- |
| ภาษาไทย | `สวัสดี กรุณาตอบสั้น ๆ` | `สวัสดีครับ! ยินดีช่วยเหลือค่ะ 😊` | 7,302 ms | [JSON](ollama-contract-thai.json) |
| ภาษาอังกฤษ | `What is the capital of Thailand? Answer briefly.` | `Bangkok` | 249 ms | [JSON](ollama-contract-english.json) |
| คำนวณพื้นฐาน | `หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ` | `2` | 200 ms | [JSON](ollama-contract-math.json) |

คำถามแรกหลังเริ่ม runtime ช้ากว่าคำถามถัดไปเพราะมี model load/warm-up จึงไม่ใช้เวลารอบหลังเป็นค่ารับประกัน production

## Unavailable endpoint

เรียก port ว่าง `127.0.0.1:11435` โดยไม่หยุด Ollama ที่ใช้งานอยู่ สคริปต์แสดง `FAIL while checking Ollama availability`, แนะนำให้เปิด Ollama/ตรวจ `/api/version` และคืน exit code `1` ตาม contract ดู [ผลจริง](ollama-contract-unavailable.txt)

## Regression failure tests

คำสั่ง:

```powershell
py -3 -m unittest discover -s tests -v
```

ผลล่าสุดหลังแก้ Contract ตาม Review คือ `Ran 9 tests in 19.539s`, `OK` โดย test method แบบ subtest ครอบคลุม 21 scenarios และใช้ mock HTTP เฉพาะ `127.0.0.1`:

- success, UTF-8 prompt, request contract และ evidence output
- `done=false`, `done` เป็น string และ `done_reason=length`
- model ผิดหรือชนิดผิด
- response ไม่ใช่ JSON, root เป็น array, ไม่มี `answer`, key ผิด case, answer ว่าง, field เกิน และ think marker
- version metadata ผิด, HTTP 404/model not found
- server delay 3 วินาทีเทียบกับ client timeout 1 วินาที ต้องคืน exit `1`
- unavailable endpoint ต้องคืน exit `1`
- path หลักฐานใช้ไม่ได้, การแทนไฟล์เดิมเมื่อสำเร็จ และรักษาหลักฐานเดิมเมื่อล้มเหลว

mock timeout ยืนยันการตรวจจับและ exit behavior ของ client โดยไม่ต้องทำให้ live model ค้างโดยตั้งใจ ส่วน live unavailable ยืนยัน connection failure กับเครื่องจริง

รัน `dotnet test .\BC-LocalAI.slnx` หลังแก้ Contract แล้วผ่าน 17 tests, ล้มเหลว 0, ข้าม 0 เพื่อยืนยันว่า AI-002 ไม่ทำให้ชุดทดสอบ Backend ที่รวมเข้ามาใน main เสีย

## Reproduce

ดูคำสั่ง request ทั้งสามกลุ่ม, unavailable endpoint และ regression suite ใน [Integration Contract](ollama-integration-contract.md) หลักฐานไม่มี secret, token หรือข้อมูลส่วนตัว และบันทึกเฉพาะ prompt ทดสอบทั่วไปกับ response ของโมเดล
