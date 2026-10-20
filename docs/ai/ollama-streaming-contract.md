# AI-005: Ollama Streaming Contract

สเปกนี้กำหนดรูปแบบการเรียก Ollama แบบ streaming (`POST /api/generate`, `stream:true`) กับ `qwen3:4b` กฎการอ่าน NDJSON การตัดสินว่าสำเร็จ การจัดการ error การยกเลิก และ **Application Event Contract ที่เสนอสำหรับ BE-004/FE-002** ทุกข้อเขียนจากการทดสอบจริงกับ Ollama 0.40.5 (ดู [ผลทดสอบ](ollama-streaming-test-results.md) และ [หลักฐาน JSON](ollama-streaming-evidence.json)) ข้อที่ยังเป็นข้อเสนอหรือรอการตัดสินใจระบุไว้ชัดเจน คู่มือ implement สำหรับ Backend อยู่ใน [Handoff](ollama-streaming-handoff.md) เอกสารนี้ต่อยอดจาก [AI-002 Integration Contract](ollama-integration-contract.md) และ **ไม่แก้ Backend ใด ๆ**

## 1. สรุปการตัดสินใจ

| # | ข้อสรุป | หลักฐาน |
| --- | --- | --- |
| 1 | ใช้คำขอเดียวกับ AI-002 เปลี่ยนแค่ `stream:true` โดย **คง `format` JSON schema `{answer:string}` และ `think:false`** | S06 |
| 2 | **ห้าม stream แบบข้อความล้วน (ไม่มี `format`)**: โมเดลคิดออกเสียงลงใน `response` โดยไม่มีแท็ก `<think>` จึงตรวจจับด้วย marker ไม่ได้ และกิน token budget (คำถามภาษาไทยถูกตัดที่ 256 token ก่อนจะตอบ) | S06 |
| 3 | `response` ของแต่ละ record เป็นเศษ JSON จึงต้องมี **ตัวถอดค่า `answer` ทีละส่วนแบบจำสถานะ** ชื่อ key และ escape ถูกแบ่งข้าม record ได้ ภาษาไทยถูกแบ่งกลางพยางค์ได้ | S01–S04 |
| 4 | สำเร็จ (`completed`) ต่อเมื่อมี record สุดท้าย `done:true` + `done_reason:"stop"` + JSON ของ `answer` ครบและไม่ว่าง เท่านั้น | S07, S09, S14 |
| 5 | Error กลางสตรีมมาเป็น record `{"error": ...}` หลังส่ง HTTP 200 ไปแล้ว ข้อความมีรายละเอียดภายใน **ห้ามส่งต่อผู้ใช้** | S14 |
| 6 | ยกเลิกได้จริง Ollama หยุด generate เมื่อ client ปิด connection แต่ **.NET ค่าเริ่มต้นหน่วง connection ไว้ ~2 วินาที** ต้องตั้ง `MaxResponseDrainSize = 0` | S10, S11 |
| 7 | Ollama ส่ง **header พร้อม record แรก** (cold start เงียบ ≈ 4–5 วินาที) Backend ต้องส่ง `started` เอง | ข้อ 11 |

### ประเด็นที่รอ Tech Lead ตัดสิน

1. **ยืนยันการคง `format` ตอน stream** (ข้อ 1–2) ซึ่งไม่เปลี่ยน AI-002 contract แต่ปฏิเสธข้อเสนอ plain-text streaming ที่ผมเคยเสนอไว้ในแผนก่อนทดสอบ เพราะหลักฐานไม่รองรับ
2. **`done_reason=length` กลางสตรีม** (ข้อ 8.2): ส่งเป็น event `error` (`AI_RESPONSE_TOO_LARGE`) พร้อมธง `partial` หรือเป็น `completed` พร้อมธง `truncated` เสนอแบบแรกให้สอดคล้อง BE-003
3. ค่า timeout ของ stream (ข้อ 9) ควรแยก "รอ record แรก" "ช่องว่างระหว่าง record" และ "เวลารวม" ซึ่งเป็นการเปลี่ยน configuration ของ BE-003

## 2. Endpoint และเครือข่าย

เหมือน AI-002: `POST http://127.0.0.1:11434/api/generate` เรียกผ่าน loopback เท่านั้น (`127.0.0.1`, `localhost`, `[::1]`) ไม่เปิดให้เครื่องอื่นเรียก และไม่ตาม redirect

## 3. Streaming Request Contract

| Field | Type | ค่า | หมายเหตุ |
| --- | --- | --- | --- |
| `model` | string | `qwen3:4b` | ต้องตรงทุก record ในคำตอบ |
| `prompt` | string | ข้อความผู้ใช้ที่ไม่ว่าง | prompt ว่างได้ HTTP 200 + `done_reason:"load"` ซึ่งไม่ใช่คำตอบ (S09) |
| `stream` | boolean | **`true`** | ต่างจาก AI-002 เพียงข้อนี้ |
| `think` | boolean | `false` | ดูข้อ 7 |
| `system` | string | `Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.` | เหมือน AI-002 |
| `format` | JSON Schema | `{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}` | **ต้องมี** ดูข้อ 1–2 |
| `options.temperature` | number | `0` | |
| `options.num_predict` | integer | ค่าจาก configuration (AI-002: 256, BE-003: `MaxOutputTokens` 1024 สูงสุด 4096) | ต้องเป็น integer มิฉะนั้น Ollama ตอบ HTTP 500 (S08) |

ตัวอย่าง request:

```json
{
  "model": "qwen3:4b", "prompt": "What is the capital of Japan?", "stream": true, "think": false,
  "system": "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.",
  "format": { "type": "object", "properties": { "answer": { "type": "string" } }, "required": ["answer"], "additionalProperties": false },
  "options": { "temperature": 0, "num_predict": 1024 }
}
```

## 4. Native Ollama Response Contract (ที่สังเกตจริง)

### 4.1 การส่งข้อมูล

- HTTP 200, `Content-Type: application/x-ndjson`, `Transfer-Encoding: chunked`
- **หนึ่ง record ต่อหนึ่งบรรทัด** (คั่นด้วย `\n`) ส่วนใหญ่ **หนึ่ง token ต่อหนึ่ง record** ห่างกันเฉลี่ย 16–22 ms (≈ 50–60 tokens/s)
- ห้ามสมมติว่าหนึ่ง network read = หนึ่ง record: บรรทัดและตัวอักษร UTF-8 ถูกตัดข้าม read ได้ (ทดสอบด้วย mock)
- **Header มาพร้อม record แรกเสมอ** (warm 36–330 ms, cold ≈ 4–5 วินาที) ก่อนหน้านั้นไม่มีข้อมูลใดมาถึง

### 4.2 ชนิดของ record

| ชนิด | field | ความหมาย |
| --- | --- | --- |
| token | `model`, `created_at`, `response` (string, อาจว่าง), `done:false` | เศษของ `response` |
| thinking (เฉพาะ `think:true`) | `response:""`, `thinking` (string), `done:false` | ส่วนคิด แยกจาก `response` |
| final | `done:true`, `done_reason`, `response:""`, `context` (int[] ขนาดใหญ่ ไม่ใช้), `total_duration`, `load_duration`, `prompt_eval_count`, `prompt_eval_cached_count`, `prompt_eval_duration`, `eval_count`, `eval_duration` (ns) | จบสตรีม |
| error | `{"error": "<ข้อความ>"}` เท่านั้น (ไม่มี `done`) | ล้มเหลวหลังเริ่มสตรีม |

`done_reason` ที่พบ: `stop` (จบเอง), `length` (ชน `num_predict`), `load` (prompt ว่าง ไม่ได้ generate) ผู้อ่านต้อง **ไม่ error เมื่อเจอ field ที่ไม่รู้จัก** (เช่น `context`, `prompt_eval_cached_count`) ให้ข้ามไป

ตัวอย่าง (ย่อ, จริงจาก qwen3:4b, `format` + `think:false`):

```text
{"model":"qwen3:4b","created_at":"...","response":"{\"","done":false}
{"model":"qwen3:4b","created_at":"...","response":"answer","done":false}
{"model":"qwen3:4b","created_at":"...","response":"\":","done":false}
{"model":"qwen3:4b","created_at":"...","response":" \"","done":false}
{"model":"qwen3:4b","created_at":"...","response":"กร","done":false}
{"model":"qwen3:4b","created_at":"...","response":"ุง","done":false}
...
{"model":"qwen3:4b","created_at":"...","response":"\"}","done":false}
{"model":"qwen3:4b","created_at":"...","response":"","done":true,"done_reason":"stop","eval_count":13,...}
```

### 4.3 Error ก่อนเริ่มสตรีม (HTTP status ปกติ)

| กรณี | HTTP | body | พบใน |
| --- | --- | --- | --- |
| ไม่มีโมเดล | 404 | `{"error":"model '…' not found"}` | S08 |
| JSON ของ request เสีย | 400 | `{"error":"unexpected EOF"}` | S08 |
| option ผิดชนิด / format ผิด | **500** | `{"error":"option \"num_predict\" must be of type integer"}` | S08 |
| path ผิด | 404 | `404 page not found` (text/plain) | probe |
| prompt ว่าง | **200** | `{"…","response":"","done":true,"done_reason":"load"}` (`application/json`) | S09 |

ข้อสังเกต: ข้อผิดพลาดฝั่ง client (option/format ผิด) ถูกตอบเป็น **500** ไม่ใช่ 400 และ prompt ว่าง "สำเร็จ" แบบไม่มีคำตอบ จึงตัดสินจาก `done_reason` เสมอ ไม่ใช่จาก HTTP status อย่างเดียว

## 5. การอ่านสตรีม (กฎสำหรับ Backend)

1. เปิดคำขอแบบอ่าน header ก่อน (`HttpCompletionOption.ResponseHeadersRead`) แล้วอ่าน body เป็น stream
2. ถอดไบต์เป็นข้อความด้วย **UTF-8 decoder แบบจำสถานะ** (ตัวอักษรไทย 3 ไบต์และ emoji 4 ไบต์อาจถูกตัดกลางระหว่าง read)
3. ตัดบรรทัดที่ `\n` เก็บเศษบรรทัดที่ยังไม่จบไว้ใน buffer ข้าม read
4. parse แต่ละบรรทัดเป็น JSON object ถ้าไม่ใช่ → `invalid` ไม่ใช่ข้าม
5. ตรวจทุก record: มี `error` → `provider_error`; `model` ต้องเป็น string ตรงกับที่ขอ; `done` ต้องเป็น boolean; `response` (ถ้ามี) ต้องเป็น string; `thinking` นับแต่ไม่ส่งต่อ
6. หลัง record `done:true` ต้องไม่มีข้อมูลเหลือ (นอกจากบรรทัดว่าง)
7. ถ้าสตรีมจบ (EOF หรือ connection หลุด) โดยไม่มี `done:true` → `interrupted`

## 6. การถอดค่า `answer` ทีละส่วน

`response` ต่อกันได้เป็น `{"answer": "<ข้อความ>"}` แต่ละ fragment ตัดตามขอบ token ไม่ใช่ขอบ JSON ตัวอย่างจริงที่ตัวถอดต้องรับมือ:

| สิ่งที่พบ | ตัวอย่าง fragment ติดกัน |
| --- | --- |
| ชื่อ key ถูกแบ่ง | `a` `ns` `we` `r` `":` |
| escape ถูกแบ่งข้าม record | `\` แล้วตามด้วย `":[` (จาก `\"`) |
| ภาษาไทยถูกแบ่งกลางพยางค์ | `กร` `ุง` `เทพ` `ม` `ห` `าน` `คร` |
| emoji / ไทยดิบ ไม่ escape | ` 😊` `🎉` ` ` `คร` `ับ` |
| escape ของ JSON | `\"` `\n` `\\` (ไม่พบ `\uXXXX` แต่ต้องรองรับ) |

ตัวถอด (state machine ที่ใช้ใน `scripts/OllamaStreaming.psm1`, ผ่านการทดสอบกับ fragment จริงข้างต้น):

```text
start --'{'--> key_or_end --'"'--> key --'"'--> after_key --':'(key = answer เท่านั้น)--> value
value --'"'--> answer (ส่ง delta ทีละตัวอักษรที่ถอด escape แล้ว)
answer --'"'--> after_answer --'}'--> done   (ที่เหลือต้องเป็นช่องว่าง)
```

กฎที่ทำให้ปฏิเสธ (outcome `invalid`): ไม่ขึ้นต้นเป็น `{` (เช่นข้อความล้วน), key อื่นนอกจาก `answer` หรือ `answer` ซ้ำ/มี field เกิน, ค่าไม่ใช่ string, escape ไม่ถูกต้อง, มีอักขระควบคุมดิบ (เช่นขึ้นบรรทัดใหม่) ใน string, มีข้อมูลหลัง `}`, คำตอบว่าง หรือมี `<think>`/`</think>` ผลลัพธ์ที่ถอดต้องเท่ากับ `answer` ที่ได้จากการ parse JSON ที่ประกอบครบ (ตรวจในทุก scenario)

เฉพาะค่าของ `answer` เท่านั้นที่ส่งเป็น delta ส่วนโครงสร้าง `{ "answer": "` และ `" }` ไม่ถูกส่ง

## 7. Thinking behavior

| คำขอ | ผลที่พบ | สรุป |
| --- | --- | --- |
| `think:false` + `format` | ไม่มี field `thinking` ไม่มีเนื้อหาคิดใน `response` (S01–S04) | **ใช้แบบนี้** |
| `think:false` **ไม่มี** `format` | `response` เริ่มด้วยข้อความคิด (`Okay, the user is asking…`) ไม่มีแท็ก `<think>` (S06 ยืนยันว่า record แรกเป็นร้อยแก้วและถูกปฏิเสธ) ในการทดลองสำรวจ (ไม่อยู่ในหลักฐาน JSON) คำถามอังกฤษใช้ 230 record จึงจบ ส่วนคำถามภาษาไทยชน 256 token (`done_reason:length`) ก่อนตอบ | **ห้ามใช้** ตรวจด้วย marker ไม่ได้ |
| `think:true` (+ `format` หรือไม่ก็ได้) | ส่วนคิดมาใน field `thinking` (812 อักขระ, 188 record สำหรับคำถามง่าย) `response` ว่างจนกว่าจะคิดเสร็จ คำตอบแรกมาที่ ≈ 3.0 วินาที (S05) ส่วนคิดกิน `num_predict` | ใช้ได้ถ้าจำเป็น แต่ช้ากว่าและเสี่ยงชนเพดาน token |

กฎ: field `thinking` ต้อง **ไม่ถูกส่งต่อ** ไปยังผู้ใช้และไม่ปนใน `answer` (นับเฉพาะจำนวนอักขระ) คำตอบที่มี `<think>`/`</think>` ถือว่า `invalid`

เหตุที่ `format` ป้องกันการคิดรั่ว: schema บังคับให้โมเดลเริ่มสร้าง `{` ทันที (`context` ของ record สุดท้ายแสดงว่า template เปิดบล็อกคิวแล้วตามด้วย JSON ทันที) ผลคือเร็วที่สุด (record แรก ≈ 50 ms, ตอบจบ ≈ 250–650 ms)

## 8. Completion rules และการจัดประเภท

### 8.1 เงื่อนไข `completed`

ต้องครบทุกข้อ: HTTP 200 · record สุดท้ายมี `done:true` · `done_reason == "stop"` · ทุก record ผ่านข้อ 5 · ตัวถอดจบสมบูรณ์ (พบ `}`) โดยไม่มี error · `answer` ไม่ว่าง · ไม่มี `<think>` ถ้าไม่ครบ **ห้ามส่ง `completed`** (ส่ง `error` แทน)

### 8.2 ตารางผลลัพธ์

| outcome | เงื่อนไข | รหัส error (ชื่อตาม BE-003) | พบใน |
| --- | --- | --- | --- |
| `completed` | ตามข้อ 8.1 | — | S01–S04 |
| `truncated` | `done:true` + `done_reason:"length"` (JSON ไม่ครบ) | `AI_RESPONSE_TOO_LARGE` | S07 |
| `provider_error` | พบ record `{"error": ...}` | `AI_PROVIDER_ERROR` | S14 |
| `http_error` | HTTP ≠ 200 ก่อนเริ่มสตรีม | `AI_PROVIDER_ERROR` | S08 |
| `interrupted` | สตรีมจบ/connection หลุดโดยไม่มี `done:true` | `AI_INVALID_RESPONSE` | mock |
| `invalid` | record/โครงสร้างผิด `done_reason` อื่น (`load` ฯลฯ) คำตอบว่าง/ไม่ครบ/มี think marker model ไม่ตรง | `AI_INVALID_RESPONSE` | S06, S09 |
| `timeout` | เกิน total หรือ idle timeout | `AI_TIMEOUT` | S11 |
| `unavailable` | ต่อไม่ได้ (connection refused) | `AI_UNAVAILABLE` | S12 |
| `cancelled` | client ยกเลิก | — (ไม่ใช่ error) | S10 |

**`done_reason=length` กลางสตรีม:** deltas ถูกส่งออกไปแล้ว ดึงกลับไม่ได้ และ HTTP 200 ถูกส่งไปแล้วจึงตอบ 502 ไม่ได้ ข้อเสนอ: ส่ง event `error` รหัส `AI_RESPONSE_TOO_LARGE` พร้อม `partial:true` (ไม่ใช่ `completed`) ให้ UI คงข้อความบางส่วนไว้และแจ้งผู้ใช้ว่าคำตอบถูกตัด (รอ Tech Lead ยืนยัน)

### 8.3 ข้อความ error จาก Ollama

ข้อความใน record `error` มีรายละเอียดภายใน เช่น `an error was encountered while running the model: read tcp 127.0.0.1:<port>-><…>: wsarecv: An existing connection was forcibly closed by the remote host.` **ห้ามส่งต่อผู้ใช้หรือบันทึกลง log แบบดิบ** ส่ง `message` ทั่วไปที่ปลอดภัยตามรหัส (เช่น "AI service returned an error") ส่วนใน log ให้ตัด endpoint และ path ออก (ฟังก์ชัน `ConvertTo-SafeText`)

## 9. Timeout

ค่าที่วัดได้ (S13, S00, S03): record แรก warm 36–330 ms (มัธยฐาน 37 ms ใน S13); **cold start 4.1–5.1 วินาที** ที่เงียบสนิท; ช่องว่างระหว่าง record เฉลี่ย 16–34 ms สูงสุดที่พบ 58 ms ข้อเสนอ (ยังไม่ได้เป็นค่า configuration):

| Timeout | ความหมาย | ข้อเสนอเริ่มต้น |
| --- | --- | --- |
| รอ record แรก | รวมโหลดโมเดล | ≥ 30 วินาที (cold start ปกติ 4–5 วินาที แต่ Ollama รอโหลดได้ถึง `OLLAMA_LOAD_TIMEOUT` 5 นาทีเมื่อเครื่องกดดัน) |
| ช่องว่างระหว่าง record (idle) | หลัง record แรก | 15–30 วินาที (ปกติ < 0.1 วินาที) |
| เวลารวม (total) | ตลอดคำขอ | ค่า `Ollama:TimeoutSeconds` เดิม (180) |

เมื่อเกิน timeout ให้ยกเลิกคำขอไป Ollama ด้วย token (ข้อ 10) และส่ง `error` รหัส `AI_TIMEOUT` ทดสอบแล้วว่าเมื่อ total timeout ตัดคำขอ คำขอถัดไปได้ทันที (S11: 332 ms)

## 10. Cancellation

| ทดสอบ | ผล | สรุป |
| --- | --- | --- |
| ปิด connection กลางสตรีมทันที (`MaxResponseDrainSize = 0`) แล้วส่งคำขอถัดไป | record แรกของคำขอถัดไปที่ **74 ms** (S10) | Ollama หยุด generate และว่างทันที |
| ยกเลิกด้วย `CancellationToken` กลางการอ่าน (total timeout) | คำขอถัดไป 332 ms ทันที (S11) | ใช้ได้ |
| `Dispose()` response ที่อ่านไม่จบ ด้วยค่าเริ่มต้นของ `SocketsHttpHandler` | คำขอถัดไปรอ **≈ 2,070 ms** (วัดซ้ำ 4 ครั้งได้ 2,077–2,099 ms ไม่ขึ้นกับจำนวน record ที่อ่านแล้ว) | **กับดัก**: .NET พยายาม "ดูดทิ้ง" body ที่เหลือสูงสุด `ResponseDrainTimeout` 2 วินาที connection ยังเปิด และ Ollama ยัง generate ต่อ |

กฎสำหรับ Backend: (1) เมื่อ client ตัดการเชื่อมต่อให้ยกเลิก request ไป Ollama ด้วย `CancellationToken` (2) ตั้ง `SocketsHttpHandler.MaxResponseDrainSize = 0` ให้ `HttpClient` ที่เรียก Ollama เพื่อให้ `Dispose` ปิด connection ทันที (3) ไม่ใช้ connection ซ้ำสำหรับสตรีมที่ถูกยกเลิก ผลทดสอบข้อ 3 ทำซ้ำได้ด้วย `-DrainOnDispose` ใน `scripts/OllamaStreaming.psm1`

หมายเหตุขอบเขตการทดสอบ: ทดสอบกับ Ollama จริงที่การยกเลิกหลัง record ที่ 10, 25 และ 100 (คำขอแบบ `think:true` มีและไม่มี `format`) ผลหน่วง 2 วินาทีเหมือนกันทั้งหมด ไม่ขึ้นกับจุดที่ยกเลิก ส่วนที่ record ที่ 5 ทดสอบกับ mock ไม่ได้ทดสอบกรณี `Cancel()` ระหว่างสอง read แล้วค่อย `Dispose` (ใช้ `MaxResponseDrainSize = 0` ซึ่งทดสอบแล้วว่าแก้ปัญหา)

## 11. ความเงียบก่อน record แรก (ข้อมูลสำหรับ SSE)

เพราะ header มาพร้อม record แรก Backend จะไม่ได้รับอะไรจาก Ollama เลยราว 4–5 วินาทีเมื่อโมเดลถูก unload (`KEEP_ALIVE` 5 นาที) ถ้า Backend รอ header ก่อนตอบ SSE ผู้ใช้จะเห็นการเชื่อมต่อค้างช่วงนั้น ข้อเสนอ: Backend ส่งหัว SSE และ event `started` ทันที (ก่อนต่อ Ollama สำเร็จ) และ error ที่เกิดก่อน record แรก (Ollama ไม่ทำงาน, 404, 500) ส่งเป็น event `error` ไม่ใช่ HTTP status (ซึ่งเปลี่ยนไม่ได้แล้ว) หรืออีกทางคือหน่วงการส่งหัว SSE จนกว่าจะได้ header ซึ่งทำให้ผู้ใช้เห็นค้าง 4–5 วินาทีเมื่อ cold เป็นเรื่องของ BE-004 ตัดสิน

## 12. Application Event Contract (ข้อเสนอสำหรับ BE-004 / FE-002)

ใช้ Server-Sent Events ตาม BE-004 (`POST /api/chat/stream`, `Content-Type: text/event-stream`) แต่ละ event เป็น `event: <ชนิด>` และ `data: <JSON>` กฎสำคัญ: **มี event ปลายทางได้เพียงหนึ่งเดียว** (`completed` หรือ `error` หรือ `cancelled`) และต้องไม่ส่ง `completed` ถ้าข้อ 8.1 ไม่ครบ

| event | data | เมื่อไหร่ | แมปจาก Ollama |
| --- | --- | --- | --- |
| `started` | `{"model":"qwen3:4b"}` | เริ่มประมวลผลคำขอ | ก่อน/พร้อมเริ่มอ่านสตรีม |
| `delta` | `{"text":"<ส่วนของคำตอบ>"}` | ได้ข้อความเพิ่ม | ผลจากตัวถอดข้อ 6 (ไม่มี JSON scaffolding ไม่มี thinking) |
| `completed` | `{"finishReason":"stop"}` (อาจเพิ่ม `evalCount`) | ครบตามข้อ 8.1 | record `done:true` + `done_reason:"stop"` |
| `error` | `{"code":"AI_…","message":"<ข้อความทั่วไปที่ปลอดภัย>","partial":true\|false}` | outcome อื่นที่ไม่ใช่ completed/cancelled | ตารางข้อ 8.2 (`partial:true` เมื่อมี delta ส่งไปแล้ว) |
| `cancelled` | `{}` | ผู้ใช้ยกเลิกและ connection ยังส่งได้ | client ยกเลิก (ส่วนใหญ่ connection หายไปแล้ว จึงไม่ต้องรับประกันว่าจะส่งได้) |

ข้อกำหนดเพิ่ม: `text` ใน `delta` ประกอบกันตามลำดับเท่ากับคำตอบสุดท้ายเสมอ · ไม่มีข้อมูลข้ามคำขอ (หนึ่ง stream ต่อหนึ่งคำขอ ห้ามใช้ state ร่วม) · `message` ไม่มีรายละเอียดภายในของ Ollama · Backend อาจรวม delta หลาย token ก่อนส่ง (ไม่ได้ทดสอบ) · `POST /api/chat` เดิมคงเดิม

## 13. ข้อมูลประสิทธิภาพของ streaming (เครื่อง RTX 4050 6 GB, Ollama 0.40.5)

| ค่า | ผล | ที่มา |
| --- | --- | --- |
| record แรก (warm) | มัธยฐาน 37 ms (36–94 ms) | S13 |
| ข้อความคำตอบแรกถึง client (`format`) | มัธยฐาน 155 ms (143–301 ms) | S13 |
| ตอบจบทั้งคำตอบสั้น (warm) | มัธยฐาน 233 ms (215–401 ms) | S13 |
| record แรกหลัง `ollama stop` (cold) | 4,144 ms (S00) / 3,955 ms (ทดสอบแยก) | S00 |
| ช่องว่างเฉลี่ยระหว่าง record | 16–34 ms (≈ 50–60 tokens/s) | S01–S05, S13 |
| `think:true` คำตอบแรก | 2,971–3,048 ms (คิด 188 record) | S05 |

ค่าเหล่านี้คือ time-to-first-token ที่ AI-004 วัดไม่ได้เพราะเป็น non-streaming เป็นผลบนเครื่องเดียวและตัวอย่างน้อย (n=5 สำหรับ S13) ใช้บอกลำดับความสำคัญ ไม่ใช่ SLA

## 14. ข้อจำกัดและสิ่งที่ยังไม่ได้ทดสอบ

- ทดสอบกับ Ollama 0.40.5 และ `qwen3:4b` บนเครื่องเดียว พฤติกรรมอาจต่างในเวอร์ชันหรือโมเดลอื่น (เช่นรูปแบบ error กลางสตรีม และพฤติกรรมของ `think`)
- **Interrupted stream ของจริง** ทดสอบด้วยการหยุด process runner (`llama-server.exe`) กลางสตรีมเท่านั้น (S14, ตัวเลือก `-IncludeRunnerStop`) ผลคือ record `{"error": ...}` ไม่ใช่การตัด TCP ดิบ กรณีหลังจำลองด้วย mock เท่านั้น
- ไม่ได้ทดสอบหลายสตรีมพร้อมกัน (Ollama ตอบทีละคำขอตาม AI-004) และไม่ได้ทดสอบผ่าน reverse proxy ที่อาจ buffer SSE
- ไม่ได้วัดผลของการรวม delta, ขนาด SSE overhead หรือ keep-alive comment
- ค่า timeout ในข้อ 9 เป็นข้อเสนอจากการสังเกต ไม่ใช่ค่าที่ทดสอบเป็นเกณฑ์
- `\uXXXX` ไม่เคยพบในผลจริง (รองรับและทดสอบด้วย mock เท่านั้น)
- ชุดคำถามทดสอบเล็ก ผ่านทุก scenario ไม่ได้รับรองว่าโมเดลตอบถูกต้อง (ขอบเขตเดียวกับ AI-003)

## 15. Reproduce

ต้องมี PowerShell 7, Ollama และ `qwen3:4b` (เฉพาะ loopback):

```powershell
# ทุก scenario พื้นฐาน (13 scenarios, exit 0 เมื่อผ่านทุก check)
pwsh -NoProfile -File .\scripts\test-ollama-streaming.ps1

# รวมโหมดเสริม: cold start (ollama stop) และหยุด runner กลางสตรีม (failure injection)
pwsh -NoProfile -File .\scripts\test-ollama-streaming.ps1 -IncludeColdStart -IncludeRunnerStop

# เทสต์ที่ไม่ต้องใช้โมเดล (mock)
py -3 -m unittest tests.test_ollama_streaming -v
```

สคริปต์เขียนหลักฐานที่ `docs/ai/ollama-streaming-evidence.json` (ใช้ `-OutputPath` เพื่อเลี่ยงการทับ) `-IncludeRunnerStop` หยุด process `llama-server.exe` ใต้โฟลเดอร์ Ollama ซึ่ง Ollama จะเปิดใหม่เองเมื่อมีคำขอถัดไป ห้ามใช้ถ้ามีคนอื่นใช้ Ollama เครื่องเดียวกันอยู่
