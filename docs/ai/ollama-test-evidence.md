# AI-001: Local test evidence

ทดสอบวันที่ 19 ตุลาคม 2026 (Asia/Bangkok) ใน branch `pao01_AI` จาก base commit `7640d4b17ed00affbd819c175dc2d66de9b63e73`

## Environment

| รายการ | ผลจริง |
| --- | --- |
| OS | Windows 11 Home Single Language, 10.0.26200 |
| RAM ที่ระบบรายงาน | 15.6 GiB (เครื่องระดับ 16 GB) |
| GPU | NVIDIA GeForce RTX 4050 Laptop GPU |
| VRAM จาก nvidia-smi | 6141 MiB |
| Ollama | 0.40.5; ติดตั้งอยู่ก่อนแล้ว |
| Model | qwen3:4b; ดาวน์โหลดด้วย ollama pull สำเร็จ |
| Test shell | PowerShell 7.6.5 |

**Hardware deviation:** ไม่ใช่ RTX 4060/4070 ตาม issue จึงยังต้องให้ Tech Lead รับทราบ/ยอมรับ หรือทดสอบซ้ำบนเครื่องตามสเปก ไม่ได้ทดสอบบน Windows Server และไม่ได้ทดสอบ fresh install เพราะเครื่องนี้มี runtime อยู่แล้ว

## REST API: PASS

จากราก repo:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -OutputPath .\docs\ai\ollama-test-result.json
```

- `GET /api/version`: ตอบเวอร์ชัน 0.40.5
- `POST /api/generate`: HTTP 200, model qwen3:4b, done=true, response ไม่ว่าง
- ฉบับล่าสุดหลังเพิ่ม contract checks และการเขียนไฟล์ชั่วคราว ใช้เวลา 1,204 ms; process exit 0
- response จริง `{"answer": "สวัสดีครับ!"}`; done_reason=stop คำตอบจบสมบูรณ์
- คำสั่ง README ที่ไม่ใส่พารามิเตอร์ผ่านซ้ำใน 855 ms และ exit 0
- ตรวจรุ่นสุดท้ายหลังเพิ่มการตรวจชนิด answer: README ผ่านใน 978 ms; คำถาม `หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ` ได้ answer `2`, HTTP 200, done_reason=stop ใน 798 ms และ exit 0 ดู [ผล arithmetic](ollama-test-arithmetic.json)
- ดู request/response จริงใน [JSON evidence](ollama-test-result.json)

รุ่นสุดท้ายส่ง system prompt ให้ตอบสั้นเป็นภาษาไทย, format เป็น JSON schema `{answer:string}`, think=false, temperature=0 และ num_predict=256 นี่คือ structured smoke test ไม่ใช่หลักฐานว่าทุก prompt จะตอบถูกหรือได้ภาษาไทยเสมอ ไม่ได้แก้ template ของโมเดลหรือ runtime

ก่อนปรับ schema พบข้อความคิดปะปนและคำตอบถูกตัด ต่อมาทดสอบ schema โดยไม่ส่ง think=false ก็ยังพบคำตอบถูกตัด สคริปต์ฉบับตรวจเข้มคืน exit 1 ตามที่ควรเป็น จึงใช้ชุด request ที่มีทั้ง schema และ think=false ซึ่งทดสอบแล้วผ่านสองครั้ง `/api/show` ของเครื่องนี้รายงาน thinking.values=[true] ดังนั้นไม่สรุปว่า think=false เพียงอย่างเดียวรับประกันปิด thinking ได้

## CLI: PASS (JSON output)

```powershell
ollama run qwen3:4b 'Return a JSON object with a single key greeting and value Hello.' --think=false --format json
```

ผลจริง `{"greeting": "Hello"}` และ exit 0 ดู [CLI output](ollama-cli-result.txt) คำถามอิสระที่ลองก่อนหน้านี้ใช้เวลานานและถูกยกเลิก จึงใช้ JSON output สำหรับ smoke test ที่ทำซ้ำได้

`ollama ps` หลังทดสอบรายงาน qwen3:4b, ขนาดที่โหลด 3.5 GB, context 4096, processor 55%/45% CPU/GPU จึงไม่ใช่การยืนยันว่าโมเดลทำงานบน GPU ทั้งหมด

## Endpoint unavailable: PASS

ตรวจด้วย `Get-NetTCPConnection -LocalPort 11435 -State Listen -ErrorAction SilentlyContinue` ไม่พบ listener แล้วรัน:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435
$LASTEXITCODE
```

ผลจริง:

```text
FAIL while checking Ollama availability at http://127.0.0.1:11435: No connection could be made because the target machine actively refused it. (127.0.0.1:11435)
Start Ollama and check GET /api/version on the selected port.
UNAVAILABLE_EXIT=1
```

นี่คือการจำลองปลายทางไม่พร้อมบน port ว่าง ไม่ได้ shutdown บริการที่ใช้งานอยู่

## Reproduction limits and review

Windows PowerShell 5.1 ในเครื่องนี้บล็อก `.ps1` ด้วย execution policy จึงใช้ PowerShell 7 โดยไม่เปลี่ยน policy คำสั่ง README ใช้ `pwsh` ต้องติดตั้ง PowerShell 7 และมี policy อนุญาตก่อนรัน

การทดสอบช่วงแรกที่ใช้ค่า thinking เริ่มต้นใช้เวลานาน จึงยกเลิกและทดสอบซ้ำ ผล PASS ข้างต้นเป็นรุ่น structured output ที่ตอบครบแล้ว สคริปต์ปัจจุบันไม่ให้ PASS หาก done_reason=length หรือ answer ไม่ครบรูปแบบ

ยังไม่สร้าง PR, ไม่ push และไม่ merge ตามคำสั่งผู้ใช้ การตรวจรับ hardware, Tech Lead Review และ merge จึงยังไม่เสร็จ และ Issue ยังไม่ถือว่า Done ตาม workflow ทั้งหมด

## สถานะส่งมอบสำหรับ Tech Lead

| Acceptance criterion | สถานะ |
| --- | --- |
| Ollama ทำงานและโหลด Qwen3:4b ได้ | ผ่านในเครื่องทดสอบ |
| ส่ง REST API และได้รับคำตอบ | ผ่าน greeting และ arithmetic แบบ structured output |
| Script รันตาม README | ผ่าน PowerShell 7; exit 0 |
| Error เข้าใจได้เมื่อปลายทางไม่พร้อม | ผ่าน port ว่าง; exit 1 |
| Installation Guide และ API Guide | มีครบ; ยังไม่ได้ให้สมาชิกใหม่ทดสอบ fresh install |
| ไม่มี secret ในไฟล์ที่เพิ่ม | ตรวจไฟล์และค้นรูปแบบ credential ทั่วไปแล้ว ไม่พบ |
| Hardware RTX 4060/4070 | รอทดสอบซ้ำหรือ Tech Lead ยอมรับ deviation RTX 4050 |
| PR / Tech Lead Review / Merge | ยังไม่ทำตามคำสั่งผู้ใช้ |

ไฟล์งานจำกัดอยู่ที่ README, docs/ai, scripts/test-ollama.ps1 และ tests สำหรับตรวจ script ไม่เปลี่ยน ASP.NET Backend หรือ Web UI ตรวจ `git diff --check` ผ่าน และ local main/origin/main/HEAD ยังตรง base commit ที่ระบุข้างต้น งานยังเป็น local changes ไม่มี commit ใหม่หรือการอัปโหลด

## Regression tests สำหรับตัว script

```powershell
python -m unittest discover -s tests -v
```

ผลล่าสุด: `Ran 9 tests in 17.545s`, `OK` รวม subtests เป็น 19 กรณี ใช้ HTTP จำลองบน loopback ไม่เรียกโมเดลจริง:

- success พร้อม prompt ภาษาไทย UTF-8 และ JSON evidence
- ปฏิเสธ truncated response, done ที่เป็น string, model ผิด/เป็น array, done_reason ที่เป็น array
- ปฏิเสธ response ที่ไม่ใช่ JSON, root array, key เกิน, key ผิดตัวพิมพ์, คำตอบว่าง และ think marker
- ปฏิเสธ version metadata ที่ไม่ถูกต้อง, HTTP 404, timeout และ endpoint ที่ไม่มี listener
- แจ้งข้อผิดพลาดเมื่อโฟลเดอร์ปลายทางไม่มีอยู่, แทนหลักฐานเก่าได้เมื่อสำเร็จ และรักษาหลักฐานเก่าเมื่อ generation/validation ล้มเหลว

ชุดทดสอบนี้ตรวจพบและช่วยแก้บั๊กการส่ง null ของ PowerShell ให้ File.Replace ก่อนส่งมอบ รุ่นสุดท้ายใช้ `[NullString]::Value` และผ่านทั้งกรณีไฟล์ใหม่/ไฟล์เดิม ทดสอบ live API หลังแก้แล้วผ่านตามผล REST ด้านบน Python ใช้เฉพาะ developer tests ไม่เป็น dependency สำหรับใช้งาน Ollama
