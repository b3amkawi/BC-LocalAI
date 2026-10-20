# AI-005: Backend Integration Handoff (BE-004 / FE-002)

สรุปสิ่งที่ Backend Developer ต้องนำไปใช้ implement `POST /api/chat/stream` โดยไม่ต้องเดารูปแบบข้อมูล รายละเอียดและหลักฐานอยู่ใน [Streaming Contract](ollama-streaming-contract.md) และ [ผลทดสอบ](ollama-streaming-test-results.md) AI-005 **ไม่ได้แก้ Backend** ตัวอ้างอิงที่ผ่านการทดสอบคือ `scripts/OllamaStreaming.psm1` (PowerShell) โค้ด C# ในเอกสารนี้เป็นเพียงโครงประกอบ **ไม่ได้คอมไพล์หรือทดสอบ**

## 1. วิธีเรียก Ollama

เหมือนคำขอ non-streaming ของ AI-002 และ BE-003 ทุกอย่าง เปลี่ยนเพียง `stream: true` (รายละเอียด request ในข้อ 3 ของ contract) **ต้องคง `format` JSON schema และ `think:false`** เพราะถ้าไม่มี `format` โมเดลจะคิดออกเสียงลงใน `response` โดยไม่มีแท็กให้ตรวจ (ข้อ 7 ของ contract) `num_predict` ใช้ค่า `Ollama:MaxOutputTokens` ของ BE-003

## 2. วิธีอ่านผลลัพธ์ (ลำดับที่ต้องทำ)

1. ส่งคำขอด้วย `HttpCompletionOption.ResponseHeadersRead` แล้วอ่าน `ReadAsStreamAsync`
2. ถอดไบต์ด้วย UTF-8 decoder แบบจำสถานะ (อย่าใช้ `Encoding.GetString` ต่อ chunk) ตัวอักษรไทย 3 ไบต์และ emoji 4 ไบต์ถูกตัดข้าม read ได้
3. ตัดบรรทัดที่ `\n` เก็บเศษบรรทัดข้าม read parse ทีละบรรทัดเป็น JSON object
4. ตรวจทุก record ตามข้อ 5 ของ contract (`error`, `model`, `done`, ชนิดของ `response`) ข้าม field ที่ไม่รู้จัก (`context` ใหญ่มาก ไม่ต้องเก็บ)
5. ส่ง `response` เข้า **ตัวถอด `answer` ทีละส่วน** (ข้อ 6 ของ contract) แล้วส่งเฉพาะข้อความที่ได้เป็น `delta` ห้ามส่ง `response` ดิบ
6. ตัดสินผลที่ record `done:true` ตามข้อ 8 ของ contract

### ตัวถอด `answer` (ต้องเขียนเอง ไม่ใช้ `JsonDocument` กับเศษ JSON)

ใช้ state machine ตามข้อ 6 ของ contract ที่ต้องรับมือกับสิ่งที่พบจริง: ชื่อ key ถูกแบ่ง (`a` `ns` `we` `r`), `\` ท้าย record ตามด้วย `":` ใน record ถัดไป (ต้อง buffer escape ที่ยังไม่ครบข้าม record), ภาษาไทยถูกแบ่งกลางพยางค์ (`กร` + `ุง`) ใช้ `StringBuilder` ต่อสตริงที่ถอดแล้ว เป็นฟังก์ชัน pure ที่ทดสอบได้ง่าย (ดูชุดทดสอบ `DecoderTests` ใน `tests/test_ollama_streaming.py` ซึ่งมี fragment จริงจาก qwen3:4b ใช้เป็น test fixture ได้เลย)

## 3. ตัดสินผลและแมปเป็น event

| ผลจาก Ollama | event ที่ส่งให้ client | รหัส error |
| --- | --- | --- |
| `done:true`, `done_reason:"stop"`, JSON ครบ ไม่ว่าง ไม่มี think marker | `completed` | — |
| `done_reason:"length"` | `error` + `partial:true` (**รอ Tech Lead ยืนยัน**) | `AI_RESPONSE_TOO_LARGE` |
| record `{"error":…}` | `error` | `AI_PROVIDER_ERROR` |
| HTTP ≠ 200 ก่อนมี record | `error` | `AI_PROVIDER_ERROR` |
| สตรีมจบ/หลุดโดยไม่มี `done:true` | `error` | `AI_INVALID_RESPONSE` |
| record ผิดรูป, `done_reason` อื่น (เช่น `load`), คำตอบว่าง/ไม่ครบ/มี `<think>`, model ไม่ตรง | `error` | `AI_INVALID_RESPONSE` |
| เกิน timeout | `error` | `AI_TIMEOUT` |
| ต่อ Ollama ไม่ได้ | `error` | `AI_UNAVAILABLE` |
| client ยกเลิก | `cancelled` (ถ้าส่งได้) | — |

กฎสำคัญ: **มี event ปลายทางเพียงหนึ่งเดียวต่อสตรีม**, **ห้ามส่ง `completed` ถ้าไม่ครบเงื่อนไข**, และ **`message` ใน `error` ต้องเป็นข้อความทั่วไป** (ข้อความใน record `{"error":…}` ของ Ollama มีพอร์ตและรายละเอียดภายใน ห้ามส่งต่อหรือ log แบบดิบ)

ข้อควรจำ: ถ้า error เกิดหลังส่ง `delta` ไปแล้ว ข้อความที่ส่งไปแล้วดึงกลับไม่ได้ UI ต้องจัดการเป็นคำตอบบางส่วน (`partial:true`)

## 4. HttpClient: การยกเลิกและกับดัก drain 2 วินาที

ผลทดสอบจริง: ถ้า `Dispose()` response ที่ยังอ่านไม่จบด้วยค่าเริ่มต้นของ `SocketsHttpHandler` connection จะถูกเก็บไว้ถึง 2 วินาทีเพื่อ "ดูดทิ้ง" ข้อมูลที่เหลือ ระหว่างนั้น **Ollama ยัง generate ต่อ** และคำขอถัดไปรอเพิ่ม ≈ 2.1 วินาที (S10) ยกเลิกด้วย `CancellationToken` กลางการอ่านไม่เจอปัญหานี้ (S11)

โครงที่เสนอ (ไม่ได้ทดสอบ C# ตัวนี้ แต่การตั้ง `MaxResponseDrainSize = 0` ทดสอบแล้วว่าแก้ปัญหาด้วย .NET ผ่าน PowerShell):

```csharp
// สำหรับ HttpClient ที่เรียก Ollama (เช่นผ่าน AddHttpClient(...).ConfigurePrimaryHttpMessageHandler)
new SocketsHttpHandler { MaxResponseDrainSize = 0 /* Dispose ปิด connection ทันที */ }

// ส่ง HttpContext.RequestAborted (รวมกับ timeout) ไปทุกขั้น: SendAsync, ReadAsStreamAsync, ReadAsync
// เมื่อ client ตัดการเชื่อมต่อ ให้ยกเลิก token นี้ แล้ว Dispose response
```

- ใช้ `HttpCompletionOption.ResponseHeadersRead` เสมอ (ไม่งั้น `HttpClient` บัฟเฟอร์ทั้ง body)
- ไม่ใช้ connection ซ้ำกับสตรีมที่ถูกยกเลิก
- Ollama รับทีละคำขอ (`NUM_PARALLEL=1`) คำขอที่รอคิวจึงเริ่มช้าลง (AI-004) ควรแยก timeout "รอ record แรก" ออกจาก "เวลารวม"

## 5. Timeout (ข้อเสนอ จากผลวัด)

| ชนิด | ค่าที่สังเกต | ข้อเสนอเริ่มต้น |
| --- | --- | --- |
| รอ record แรก | warm 36–330 ms; **cold start 4–5 วินาทีที่เงียบสนิท** | ≥ 30 วินาที |
| ช่องว่างระหว่าง record | เฉลี่ย 16–34 ms สูงสุดที่พบ 58 ms | 15–30 วินาที |
| เวลารวม | — | `Ollama:TimeoutSeconds` (180) |

เมื่อโมเดลถูก unload (`KEEP_ALIVE` 5 นาที) ผู้ใช้จะเจอความเงียบ 4–5 วินาที ควรส่ง `started` ทันที (ก่อนได้ header จาก Ollama เพราะ **header มาพร้อม record แรก**) และอาจส่ง SSE comment (keep-alive) เพื่อไม่ให้ proxy ตัด

## 6. Event Contract ที่เสนอ (สำหรับ FE-002)

รายละเอียดเต็มในข้อ 12 ของ contract

| event | data |
| --- | --- |
| `started` | `{"model":"qwen3:4b"}` |
| `delta` | `{"text":"..."}` ต่อกันตามลำดับเท่ากับคำตอบสุดท้าย |
| `completed` | `{"finishReason":"stop"}` |
| `error` | `{"code":"AI_…","message":"…","partial":true\|false}` |
| `cancelled` | `{}` |

สำหรับ FE-002 (พัฒนาด้วย mock SSE ได้ก่อน BE-004): แสดง `delta` ต่อใน bubble เดิม ห้ามสร้าง bubble ใหม่ต่อ delta · `text` มีขึ้นบรรทัดใหม่ `\n` และอักขระพิเศษ (`"`, `<`, `&`, emoji) ต้อง escape ก่อนใส่ DOM (ป้องกัน XSS: ในการทดลองสำรวจพบว่าโมเดลคืน `<script>…</script>` เป็นข้อความใน `answer` ได้) · ภาษาไทยถูกแบ่งกลางพยางค์ระหว่าง delta ได้ (`กร` แล้ว `ุง`) การต่อสตริงตามลำดับให้ผลถูกเสมอ แต่อย่าตัดหรือ normalize ที่ขอบ delta · `error` หลัง delta = ข้อความบางส่วน

## 7. รายการทดสอบที่ BE-004 ควรมี (ได้จาก AI-005)

เอา scenario และ fixture จาก `tests/test_ollama_streaming.py` ไปใช้ได้ทันที (เป็นการทดสอบ contract ของ Ollama ที่ Backend ต้องจัดการเองได้):

1. บรรทัดและตัวอักษร UTF-8 ถูกตัดข้าม read 2. key และ escape ถูกแบ่งข้าม record (fragment จริงใน `DecoderTests`) 3. field `thinking` ถูกข้าม ไม่ปนใน delta 4. สตรีมจบก่อน `done:true` ต้องไม่ส่ง `completed` 5. record `{"error":…}` กลางสตรีม ต้องส่ง `error` และ `message` ไม่มีรายละเอียดภายใน 6. `done_reason` = `length` / `load` / ค่าแปลก 7. คำตอบว่าง ไม่ครบ มี `<think>` มี field เกิน 8. HTTP 404/400/500 ก่อนเริ่มสตรีม 9. ยกเลิกกลางสตรีมแล้วเซิร์ฟเวอร์จำลองเห็นว่า connection ถูกปิดทันที (ไม่ใช่หลัง 2 วินาที) 10. total และ idle timeout ปิด connection ไป Ollama 11. ไม่มีข้อมูลข้ามคำขอเมื่อมีหลาย stream พร้อมกัน (ยังไม่ได้ทดสอบใน AI-005) 12. `POST /api/chat` เดิมยังทำงานปกติ

## 8. ข้อจำกัดที่ต้องรู้

- ผลทดสอบจากเครื่องเดียว (RTX 4050) Ollama 0.40.5 และ `qwen3:4b` เท่านั้น
- ไม่ได้ทดสอบผ่าน reverse proxy, หลายสตรีมพร้อมกัน หรือ ASP.NET `RequestAborted` ตรง ๆ
- `done_reason=length` กลางสตรีม (ส่งเป็น `error` หรือ `completed` + `truncated`) รอ Tech Lead ตัดสิน และต้องสอดคล้องกับ BE-003 (`MaxOutputTokens`, `AI_RESPONSE_TOO_LARGE`) ซึ่งยังเป็น PR ที่ยังไม่ merge
- ค่า timeout เป็นข้อเสนอ ไม่ใช่ค่าที่ทดสอบเป็นเกณฑ์
- ยังไม่ได้ทดสอบการรวม delta หลาย token ก่อนส่ง SSE (ลดจำนวน event) ว่ามีผลต่อความรู้สึกของผู้ใช้อย่างไร
