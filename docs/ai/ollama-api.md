# AI-001: Ollama API guide

ส่ง `POST http://127.0.0.1:11434/api/generate` ด้วย `Content-Type: application/json; charset=utf-8`

```json
{ "model": "qwen3:4b", "prompt": "สวัสดี", "stream": false }
```

`stream: false` ทำให้ได้ JSON เดียวแทนชุดข้อความ streaming ดู [เอกสารทางการ generate](https://docs.ollama.com/api/generate) และ [โมเดล qwen3:4b](https://ollama.com/library/qwen3:4b)

## PowerShell example

```powershell
$body = @{ model = 'qwen3:4b'; prompt = 'สวัสดี'; stream = $false } | ConvertTo-Json
$http = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:11434/api/generate' `
    -Method Post -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 180
$http.StatusCode
$result = $http.Content | ConvertFrom-Json
$result.response
```

ผลที่ต้องตรวจ: HTTP 200, `model` เป็น `qwen3:4b`, `done` เป็น true และ `response` มีข้อความ โมเดลอาจส่ง `thinking` เพิ่มเติม คำตอบและระยะเวลาไม่จำเป็นต้องตรงกันทุกครั้ง

## Repeatable smoke test

จากราก repo:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Prompt 'สวัสดี' -TimeoutSec 300
```

สคริปต์ปัจจุบันใช้โมเดลคงที่ `qwen3:4b` และส่ง `think: false`, `temperature: 0`, `num_predict: 256` พร้อม system prompt ให้ตอบสั้นเป็นภาษาเดียวกับผู้ถาม และ JSON schema ที่กำหนด `answer` เป็น string ตาม [Structured Outputs](https://docs.ollama.com/capabilities/structured-outputs) ตัวอย่าง request ขั้นต่ำข้างต้นไม่ได้บังคับ schema นี้ ดู request และ validation ฉบับปัจจุบันใน [Integration Contract ของ AI-002](ollama-integration-contract.md)

หลักฐาน AI-001 เดิมใช้ system prompt ภาษาไทย ส่วน AI-002 เปลี่ยนเป็นภาษาเดียวกับผู้ถาม ไฟล์หลักฐานเก่าจึงบันทึกพฤติกรรมของ request ในรอบนั้น ไม่ใช่การทดสอบ prompt ปัจจุบันย้อนหลัง

ผล HTTP ชั้นนอกยังเป็น Ollama response ตามปกติ ส่วน `response` เป็นข้อความ JSON เช่น `{"answer":"สวัสดีครับ!"}` สคริปต์ parse JSON นี้แล้วแสดง `answer` โดยไม่ลบหรือแต่งคำตอบจริงในไฟล์หลักฐาน

สคริปต์ตรวจ HTTP 200, model, done ที่เป็น boolean true จริง, done_reason=stop และ answer ที่เป็น string ไม่ว่าง โดย JSON ต้องเป็น object ที่มี key `answer` เพียง key เดียว หากชนเพดาน token (`done_reason=length`), JSON ใช้ไม่ได้, root เป็น array หรือมี think marker ใน answer จะคืน exit 1 แทน PASS จึงไม่สับสนระหว่างรับ HTTP สำเร็จกับได้รับคำตอบครบ

สคริปต์รุ่นทบทวนต้องใช้ PowerShell 7 ขึ้นไป เพราะใช้ `System.Text.Json.JsonDocument` ตรวจชนิดและนับ key ก่อนแปลง JSON จึงปฏิเสธ key ซ้ำ เช่น `{"answer":"one","answer":"two"}` แทนการปล่อยให้ parser ยุบเหลือค่าหลังสุด ทั้ง preflight และ generate ไม่ตาม HTTP redirect (`MaximumRedirection=0`) และใช้ `NoProxy` เพื่อให้คำขอ loopback ไม่ผ่าน proxy หรือถูกพาไปยังปลายทางอื่น

พารามิเตอร์ `-Port` ใช้ทดสอบ endpoint loopback ที่ไม่พร้อม `-OutputPath` บันทึก request/response, HTTP status, เวอร์ชัน runtime และเวลาทดสอบลง JSON; โฟลเดอร์ปลายทางต้องมีอยู่ก่อน สคริปต์เขียนไฟล์ชั่วคราวในโฟลเดอร์เดียวกันให้ครบก่อนใช้ File.Replace แทนที่ไฟล์เดิม หรือ File.Move หากเป็นไฟล์ใหม่ จึงไม่ตัดไฟล์เดิมก่อนเขียน JSON ครบ หากคำขอหรือ validation ล้มเหลวจะไม่แตะหลักฐานเดิม ต้องดู exit code และเวลาของไฟล์เพื่อไม่เข้าใจผิดว่าเป็นผลรอบใหม่

ข้อจำกัดเฉพาะ runtime/model ที่ทดสอบ: `/api/show` รายงาน thinking.values=[true] แม้ชุด request ที่มี think=false ร่วมกับ schema ให้คำตอบครบได้ จึงไม่อ้างว่า flag นี้เพียงอย่างเดียวปิด thinking ได้เสมอ ตรวจ metadata และทดสอบซ้ำเมื่ออัปเดต runtime/model ตาม [Thinking controls](https://docs.ollama.com/capabilities/thinking)

ไม่ใส่ secret หรือข้อมูลส่วนบุคคลใน prompt/หลักฐาน ไฟล์ผลทดสอบเก็บ prompt และคำตอบตามจริง ตรวจเนื้อหาก่อนส่งต่อ

## Failure behavior

Connection refused หรือ timeout ไม่ใช่ HTTP 500: อาจไม่มี HTTP response เลย ตรวจข้อความ error และ listener HTTP 404 อาจหมายถึงไม่พบโมเดล ตรวจชื่อและดาวน์โหลดก่อนส่งใหม่ หาก runtime ตอบผิดรูปแบบหรือคำตอบว่าง สคริปต์ถือว่าล้มเหลว

`-TimeoutSec` จำกัดเวลา generate ต่อคำขอ ส่วน preflight `/api/version` ใช้ค่าน้อยกว่าระหว่าง 10 วินาทีกับ `-TimeoutSec` จึงไม่ใช่เพดานเวลารวมทั้ง script ข้อความ error จะแยก availability, generation, validation และการบันทึกหลักฐาน เพื่อแนะนำให้แก้ตรงจุด

## Developer regression tests

ใช้ Python 3 และ PowerShell 7 เฉพาะสำหรับทดสอบตัว script:

```powershell
python -m unittest discover -s tests -v
# หรือใช้ Python Launcher บน Windows
py -3 -m unittest discover -s tests -v
```

ชุดทดสอบเปิด HTTP จำลองบน `127.0.0.1` ด้วย port ที่ระบบเลือก แล้วปิดเมื่อจบ ตรวจ success/UTF-8/evidence, payload ผิดรูปแบบหรือถูกตัด, metadata version ไม่ถูกต้อง, model not found, timeout, endpoint ไม่พร้อม และ path บันทึกไม่ได้ ไม่เรียกโมเดลจริง ผลนี้เสริมหลักฐาน live API; ไม่ใช้แทนการทดสอบ Ollama จริง

ไม่มี authentication token ในตัวอย่างนี้ ใช้ API เฉพาะ localhost ไม่เปิด public listener งานเชื่อมต่อ Backend และการจัดการ error ฝั่งแอปเป็นงานนอกขอบเขต AI-001
