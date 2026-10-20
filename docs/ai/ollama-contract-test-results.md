# AI-002: ผลทดสอบ Ollama Integration Contract

ทดสอบวันที่ 19 ตุลาคม 2026 (Asia/Bangkok) บน Windows 11, Ollama `0.40.5`, `qwen3:4b`, PowerShell 7.6.5 และ NVIDIA RTX 4050 Laptop GPU 6141 MiB VRAM Reviewer ยืนยันแล้วว่าผล RTX 4050 ใช้รับ AI-002 ได้ เพราะ Issue นี้ตรวจ Integration Contract และไม่ได้วัดประสิทธิภาพ GPU

AI-002 ส่งเป็น [PR #8](https://github.com/b3amkawi/BC-LocalAI/pull/8) และ merge เข้า `main` แล้วที่ merge commit `b4ee48c` ตามประวัติ `origin/main` ที่ตรวจได้ในเครื่อง ผลและจำนวน tests ในเอกสารนี้เป็นหลักฐานตอนส่งมอบ AI-002 ไม่ใช่ผลล่าสุดของ AI-003 และไม่ได้รับรองว่าคำตอบจริงจาก Backend ถูกต้องครบทุกคำถาม

## Live API

ใช้ `scripts/test-ollama.ps1` เรียก `http://127.0.0.1:11434/api/generate` ผ่าน loopback เท่านั้น ทุกกรณีผ่าน HTTP 200, model `qwen3:4b`, boolean `done=true`, `done_reason=stop`, parse JSON ชั้นในได้, มี `answer` string ที่ไม่ว่างเพียง field เดียว และไม่มี think marker

| กลุ่ม | Prompt | Parsed answer | เวลา | หลักฐาน |
| --- | --- | --- | ---: | --- |
| ภาษาไทย | `สวัสดี กรุณาตอบสั้น ๆ` | `สวัสดีครับ! ยินดีช่วยเหลือค่ะ 😊` | 7,302 ms | [JSON](ollama-contract-thai.json) |
| ภาษาอังกฤษ | `What is the capital of Thailand? Answer briefly.` | `Bangkok` | 249 ms | [JSON](ollama-contract-english.json) |
| คำนวณพื้นฐาน | `หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ` | `2` | 200 ms | [JSON](ollama-contract-math.json) |

คำถามแรกใช้เวลามากกว่าคำถามถัดไป โดยอาจรวม model load/warm-up แต่ชุด AI-002 นี้ไม่ได้ควบคุมหรือยืนยันการ unload โมเดลก่อนเริ่ม จึงไม่ใช้ตัวเลขนี้เป็นค่า Cold Start ที่ยืนยันแล้วหรือค่ารับประกัน production

## Unavailable endpoint

เรียก port ว่าง `127.0.0.1:11435` โดยไม่หยุด Ollama ที่ใช้งานอยู่ สคริปต์แสดง `FAIL while checking Ollama availability`, แนะนำให้เปิด Ollama/ตรวจ `/api/version` และคืน exit code `1` ตาม contract ดู [ผลจริง](ollama-contract-unavailable.txt)

## Regression failure tests

คำสั่ง:

```powershell
py -3 -m unittest discover -s tests -v
```

ผลที่บันทึกหลังแก้ Contract ตาม Review คือ `Ran 9 tests in 19.539s`, `OK` โดย test method แบบ subtest ครอบคลุม 21 scenarios และใช้ mock HTTP เฉพาะ `127.0.0.1` จำนวนนี้เป็นผลตอนส่งมอบ AI-002; การรัน `discover` ในรุ่นใหม่อาจรวม tests ที่เพิ่มภายหลัง:

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


## Follow-up: num_predict 1024

ผลทดสอบเพิ่มเติมวันที่ 20 ตุลาคม 2026 ด้วย PowerShell 7.6.5 ผ่าน live smoke test 3/3 กรณี โดย request ส่ง num_predict=1024 จริง ดู [รายงานและ raw evidence](evidence/ai002-num-predict-1024/README.md) ผลนี้ไม่ใช่การทดสอบคำตอบยาวถึงเพดาน 1024 tokens และไม่ได้เปลี่ยน default contract 256
