# AI-002: Ollama Integration Contract

เอกสารนี้เป็น contract ที่ Backend ใช้เชื่อมต่อ Ollama `qwen3:4b` โดยไม่ต้องคาดเดารูปแบบข้อมูล ขอบเขตจำกัดที่ loopback และการตรวจ response ไม่รวม `OllamaAIService`, Controller, Backend error mapping implementation, Web UI, authentication หรือ production deployment

## Endpoint และข้อกำหนดเครือข่าย

```http
POST http://127.0.0.1:11434/api/generate
Content-Type: application/json; charset=utf-8
```

Backend ต้องเรียกผ่าน `127.0.0.1` เท่านั้น ห้ามตั้ง Ollama ให้รับจาก `0.0.0.0`, public interface, port forwarding หรือ public endpoint สำหรับ contract นี้

## Request contract

```json
{
  "model": "qwen3:4b",
  "prompt": "หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ",
  "stream": false,
  "think": false,
  "system": "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.",
  "format": {
    "type": "object",
    "properties": {
      "answer": {
        "type": "string"
      }
    },
    "required": ["answer"],
    "additionalProperties": false
  },
  "options": {
    "temperature": 0,
    "num_predict": 256
  }
}
```

| Field | Type | Required | Contract |
| --- | --- | --- | --- |
| `model` | string | yes | ต้องเป็น `qwen3:4b` |
| `prompt` | string | yes | คำถามผู้ใช้ที่ไม่ว่าง; ตัวอย่างและหลักฐานต้องไม่มี secret/ข้อมูลส่วนตัว |
| `stream` | boolean | yes | ต้องเป็น `false` เพื่อให้ได้ response เดียว |
| `think` | boolean | yes | กำหนด `false`; Backend ยังต้องตรวจ think marker ในผลลัพธ์ |
| `system` | string | yes | ให้ตอบสั้น ภาษาเดียวกับผู้ใช้ และคืน object ที่มี `answer` เท่านั้น |
| `format` | JSON Schema object | yes | บังคับ `answer:string`, required และไม่อนุญาต field เพิ่ม |
| `options.temperature` | number | yes | `0` เพื่อลดความแปรปรวน |
| `options.num_predict` | integer | yes | `256`; หากชนเพดานต้องตรวจเป็น truncated ห้ามรับเป็น success |

ส่ง body เป็น UTF-8 จริง ไม่ใช้ encoding ค่าเริ่มต้นของ Windows PowerShell 5.1 กับข้อความภาษาไทยโดยไม่ระบุ UTF-8

## Expected response และ parsed result

Ollama response ชั้นนอก:

```json
{
  "model": "qwen3:4b",
  "response": "{\"answer\":\"2\"}",
  "done": true,
  "done_reason": "stop"
}
```

`response` เป็น **string ที่ภายในเป็น JSON** Backend ต้อง parse ชั้นนอกก่อน แล้ว parse `response` อีกครั้ง ผลที่ส่งต่อภายในระบบ:

```json
{
  "answer": "2"
}
```

ห้ามอ่านข้อความ `response` ด้วยการตัด string หรือ regular expression ให้ใช้ JSON parser ทั้งสองชั้น

## Success validation

ให้ถือว่าสำเร็จเมื่อทุกเงื่อนไขต่อไปนี้ผ่านพร้อมกัน:

1. HTTP status เท่ากับ `200`
2. response ชั้นนอกเป็น JSON object
3. `model` เป็น string และตรง `qwen3:4b` แบบ case-sensitive
4. `done` เป็น boolean `true` จริง ไม่รับ string `"true"`
5. `done_reason` เป็น string `stop` แบบ case-sensitive
6. `response` เป็น string ที่ไม่ว่างและ parse เป็น JSON object ได้
7. object ภายในมี property ชื่อ `answer` แบบ case-sensitive เพียง field เดียว
8. `answer` เป็น string ที่ไม่ว่างหลัง trim
9. `answer` ไม่มี `<think>` หรือ `</think>`

เมื่อเงื่อนไขใดไม่ผ่าน ให้ถือว่าคำขอล้มเหลว ห้ามส่งคำตอบบางส่วนให้ผู้ใช้ในฐานะผลสำเร็จ

## Failure contract

ตารางนี้กำหนดการจำแนกที่ Backend ควรได้รับ ส่วน HTTP status หรือ exception ที่ Backend ส่งต่อเป็นงานของ Backend Developer

| Provider behavior | Detection | Contract classification |
| --- | --- | --- |
| ต่อ `127.0.0.1:11434` ไม่ได้ | connection refused/DNS/socket exception ก่อนมี HTTP response | `Provider unavailable` |
| เกินเวลาที่กำหนด | client timeout/cancellation | `Provider timeout` |
| Ollama ตอบ HTTP non-2xx | HTTP status ไม่ใช่ 200 | `Provider error` |
| `done=false` หรือชนิดไม่ใช่ boolean | outer validation | `Invalid/incomplete response` |
| `done_reason=length` | outer validation | `Truncated response` |
| `model` ไม่ตรงหรือชนิดผิด | outer validation | `Invalid response` |
| response ชั้นนอก/`response` ไม่ใช่ JSON object | JSON parsing/type validation | `Invalid response` |
| ไม่มี `answer`, มี field เกิน, key ตัวพิมพ์ผิด | schema validation | `Invalid response` |
| `answer` ไม่ใช่ string หรือว่าง | schema validation | `Invalid response` |
| พบ `<think>`/`</think>` | final-answer validation | `Invalid response` |

เก็บ provider error สำหรับ log ภายในตามนโยบายทีม แต่อย่านำ prompt, ข้อมูลส่วนตัว หรือรายละเอียดระบบที่ไม่จำเป็นไปแสดงต่อผู้ใช้

## Timeout guidance

- smoke script ใช้ค่าเริ่มต้น `180` วินาที และปรับได้ด้วย `-TimeoutSec`
- การโหลดโมเดลครั้งแรกอาจช้ากว่าคำขอถัดไป เครื่องทดสอบหรือเครื่องไม่มี GPU อาจต้องใช้ `300` วินาที
- preflight `GET /api/version` ใช้ค่าน้อยกว่าระหว่าง 10 วินาทีกับ timeout ที่กำหนด
- Backend ควรทำ timeout เป็น configuration และรองรับ cancellation ห้ามรอไม่จำกัด
- timeout หมายถึงไม่ทราบผลลัพธ์สุดท้าย ห้ามนำ evidence เก่าหรือ partial response มาใช้เป็นคำตอบใหม่

## Reproduce ด้วย PowerShell

จากราก Repository โดยมี Ollama, `qwen3:4b` และ PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 `
  -Prompt 'หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ' `
  -TimeoutSec 300 `
  -OutputPath .\docs\ai\ollama-contract-math.json
$LASTEXITCODE
```

ผลสำเร็จแสดง `PASS` และ exit code `0` ทุก failure แสดง stage กับคำแนะนำทาง stderr และ exit code `1` สคริปต์ไม่สร้างหลักฐาน success ใหม่เมื่อ request หรือ validation ล้มเหลว

ตัวอย่างคำถามสำหรับยืนยันสามกลุ่ม:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Prompt 'สวัสดี กรุณาตอบสั้น ๆ' -OutputPath .\docs\ai\ollama-contract-thai.json
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Prompt 'What is the capital of Thailand? Answer briefly.' -OutputPath .\docs\ai\ollama-contract-english.json
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Prompt 'หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ' -OutputPath .\docs\ai\ollama-contract-math.json
```

ทดสอบ unavailable endpoint บน port ว่างโดยไม่หยุด service จริง:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435 -TimeoutSec 5
$LASTEXITCODE
```

ผล regression สำหรับ malformed JSON, truncated response, timeout และ unavailable endpoint:

```powershell
py -3 -m unittest discover -s tests -v
```

## Model/runtime limitations

- structured output ลดความแปรปรวนแต่ไม่แทน validation ฝั่ง Backend
- `think:false` ไม่รับประกันพฤติกรรมเหมือนกันทุก Ollama/model version จึงต้องตรวจ think marker เสมอ
- `temperature:0` ไม่รับประกันข้อความเหมือนเดิมทุกครั้ง ให้ตรวจโครงสร้างและเงื่อนไข success
- คำถามจริงอาจตอบผิดเชิงเนื้อหาแม้ contract ผ่าน Contract นี้รับรองรูปแบบและ completion state ไม่ได้รับรอง factual correctness ทุกคำถาม
- ต้องทดสอบ contract ซ้ำเมื่อเปลี่ยน Ollama version, model tag, system prompt, schema หรือ generation options

ดูสรุปใน [ผลทดสอบ AI-002](ollama-contract-test-results.md), response จริงใน `ollama-contract-thai.json`, `ollama-contract-english.json` และ `ollama-contract-math.json`, ผล unavailable ใน `ollama-contract-unavailable.txt` รวมถึง regression cases ใน `tests/test_ollama_script.py`
