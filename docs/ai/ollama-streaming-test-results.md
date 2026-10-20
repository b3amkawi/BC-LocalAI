# AI-005: Ollama Streaming Test Results

ผลทดสอบจริงของ `scripts/test-ollama-streaming.ps1` กับ Ollama 0.40.5 + `qwen3:4b` (digest `359d7dd4bcda`) เมื่อวันที่ 20 ตุลาคม 2026 ผ่าน loopback เท่านั้น หลักฐานดิบอยู่ที่ [ollama-streaming-evidence.json](ollama-streaming-evidence.json) และ console output ที่ [ollama-streaming-run.txt](ollama-streaming-run.txt) สเปกที่ผลนี้ยืนยันอยู่ใน [Streaming Contract](ollama-streaming-contract.md) แยก **Expected** (สิ่งที่ contract กำหนด) ออกจาก **Actual** (ที่วัดได้) ทุกข้อ

## สรุป

- **15 scenarios, 80 checks, ผ่านทั้งหมด** exit code 0 (13 scenarios พื้นฐาน + 2 scenarios เสริม: S00 cold start และ S14 หยุด runner กลางสตรีม)
- เทสต์ที่ไม่ต้องใช้โมเดล (mock server): **27/27 ผ่าน** รวมชุดเชิงลบ 2 ชุด (เซิร์ฟเวอร์ที่คิดรั่ว และเซิร์ฟเวอร์ที่ไม่หยุดหลัง client ตัด connection ต้องทำให้สคริปต์คืน exit code 1)
- ข้อค้นพบหลัก 3 ข้อที่เปลี่ยนข้อเสนอเดิมหรือ Backend ต้องรู้:
  1. **Stream แบบข้อความล้วน (ไม่มี `format`) ใช้ไม่ได้** โมเดลคิดรั่วลง `response` โดยไม่มีแท็ก (S06) ต้องคง `format` + ตัวถอด `answer` ทีละส่วน
  2. **การยกเลิกใช้ได้ แต่ค่าเริ่มต้นของ .NET หน่วง connection ~2 วินาที** ต้องตั้ง `MaxResponseDrainSize = 0` (S10)
  3. **Error กลางสตรีมมาเป็น record `{"error":…}` หลัง HTTP 200** และข้อความมีรายละเอียดภายใน ห้ามส่งต่อผู้ใช้ (S14)

เงื่อนไขผ่านของ scenario "observation" (S06) คือ **ตัวอ่านปฏิเสธ** พฤติกรรมที่ไม่ปลอดภัย ไม่ใช่ว่าพฤติกรรมนั้นดี

## รายละเอียดแต่ละ scenario

### S01 English streaming (format + think=false), 8 checks

| Expected | Actual |
| --- | --- |
| HTTP 200, `application/x-ndjson` | 200 `application/x-ndjson` |
| หลาย record, record สุดท้าย `done:true` + `done_reason:"stop"` | 11 record, `stop` |
| `answer` ที่ถอดทีละส่วน = `answer` ของ JSON ที่ประกอบครบ | เท่ากัน (`Tokyo`) |
| ไม่มี field `thinking`, ไม่มี think marker | ไม่มี |
| เวลา | record แรก 62 ms, delta แรก 167 ms, จบ 252 ms |

### S02 Thai streaming, 8 checks

| Expected | Actual |
| --- | --- |
| คำตอบมีอักษรไทย พยางค์ที่ถูกแบ่งข้าม record ประกอบกลับได้ ไม่มี U+FFFD | `กรุงเทพมหานคร` 13 record, 7 delta, ไม่มี U+FFFD |
| เวลา | record แรก 42 ms, จบ 282 ms |

### S03 Chunk assembly, 8 checks

| Expected | Actual |
| --- | --- |
| คำตอบมาทีละส่วน (> 1 delta) delta แรกมาก่อนจบ | 32 record, **24 delta**, delta แรก 160 ms, จบ 580 ms (ประโยคยาว ≈ 25 คำ) |

### S04 Unicode และอักขระพิเศษ, 9 checks

| Prompt | Expected | Actual |
| --- | --- | --- |
| emoji + ไทย | `😊🎉` และ `ครับ` ครบ | `Hello 😊🎉 ครับ` (15 record) |
| เครื่องหมายคำพูด | `"hi"` (JSON `\"` ถอดถูก) | `He said "hi" twice` (15 record) |
| ขึ้นบรรทัดใหม่ | มี `\n` (JSON `\n` ถอดถูก) | `A\nB` (12 record) |

ไม่พบ U+FFFD ในทั้งสามกรณี

### S05 Thinking: think=true, 5 checks

| Expected | Actual |
| --- | --- |
| มี field `thinking` แยกจากคำตอบ ไม่ปนใน `answer` | มี (812 อักขระ, 188 record) `answer` = `Paris` |
| คำตอบมาถึงหลังส่วนคิด | record แรก 53 ms แต่ **คำตอบแรก 2,971 ms** (จบ 3,015 ms) |

### S06 Observation: think=false **ไม่มี** format, 3 checks

| Expected | Actual |
| --- | --- |
| ตัวอ่านปฏิเสธ (ไม่ใช่ completed) เพราะ `response` ไม่ใช่ JSON `{answer}` | outcome `invalid`, `AI_INVALID_RESPONSE`, หยุดที่ record แรก (`answer decoder: not_a_json_object`) |
| record แรกเป็นร้อยแก้วไม่ใช่ `{` | `Okay` (ข้อความคิดของโมเดล) |

### S07 Truncation, 4 checks

| Expected | Actual |
| --- | --- |
| `num_predict=8` → outcome `truncated`, `done_reason:"length"`, รหัส `AI_RESPONSE_TOO_LARGE`, JSON ไม่ครบ | ตรงทุกข้อ: 9 record, `eval_count` 8, ข้อความ `{ "answer": "Photosynthesis` ที่ parse ไม่ได้ |

### S08 HTTP errors ก่อนเริ่มสตรีม, 5 checks

| Expected | Actual |
| --- | --- |
| โมเดลไม่มี → 404 พร้อม error | 404 `model 'does-not-exist:1b' not found` |
| option ผิดชนิด → error | **500** `option "num_predict" must be of type integer` |
| JSON ของ request เสีย → 400 | 400 `unexpected EOF` |
| ทั้งหมดจัดเป็น `http_error` / `AI_PROVIDER_ERROR` | ตรง |

### S09 Prompt ว่าง, 3 checks

| Expected | Actual |
| --- | --- |
| ไม่นับเป็นสำเร็จ | HTTP **200** แต่ `done_reason:"load"` (content-type `application/json`) → `invalid` / `AI_INVALID_RESPONSE` |

### S10 Cancellation, 6 checks

| Expected | Actual |
| --- | --- |
| ยกเลิกหลัง 25 record | `cancelled` ที่ 25 record (427 ms) |
| ปิด connection ทันที → คำขอถัดไปไม่ต้องรอคิว | record แรกของคำขอถัดไป **74 ms**, จบ 367 ms (baseline 340 ms) |
| (บันทึกกับดัก) ค่าเริ่มต้นของ .NET → ต้องรอ drain | record แรกของคำขอถัดไป **2,072 ms** |

การทดลองเพิ่มนอกสคริปต์ (Ollama จริง ทำซ้ำ 3–4 ครั้งต่อกรณี): ค่า 2,065–2,100 ms คงที่ไม่ว่ายกเลิกหลัง record ที่ 10, 25 หรือ 100 และไม่ว่าคำขอมีหรือไม่มี `format` ถ้ารอ 1 วินาทีก่อนส่งคำขอถัดไป เหลือรอ ≈ 1,080 ms (สอดคล้องกับ drain 2 วินาที) และเมื่อตั้ง `MaxResponseDrainSize = 0` ได้ 71–83 ms ทุกครั้ง

### S11 Total timeout, 5 checks

| Expected | Actual |
| --- | --- |
| `timeout` ชนิด `total`, รหัส `AI_TIMEOUT`, คำขอถัดไปไม่ถูกบล็อก | timeout ที่ 2,023 ms (ตั้ง 2 วินาที), `AI_TIMEOUT`, คำขอถัดไป 332 ms |

### S12 Unavailable, 3 checks

| Expected | Actual |
| --- | --- |
| `unavailable` / `AI_UNAVAILABLE` ล้มเร็ว | ตรง แต่ **ใช้ 2,042 ms**: connection refused บน loopback ของ Windows รายงานช้ากว่าที่คาด (ไม่ใช่ทันที) |

### S13 Latency profile (warm, 5 คำขอ), 5 checks

| ค่า | Actual (มัธยฐาน) | ช่วง |
| --- | --- | --- |
| record แรก | **37 ms** | 36–94 ms |
| ข้อความคำตอบแรก | **155 ms** | 143–301 ms |
| ตอบจบ | **233 ms** | 215–401 ms |
| ช่องว่างระหว่าง record เฉลี่ย | 20–34 ms | |

คำขอแรกของชุด (94 ms / 401 ms) ช้ากว่าที่เหลือ น่าจะเป็นผลหลังโหลดโมเดลใหม่จากสถานการณ์ก่อนหน้า ไม่ได้ตรวจสอบสาเหตุ

### S00 Cold start (เสริม `-IncludeColdStart`), 3 checks

| Expected | Actual |
| --- | --- |
| หลัง `ollama stop` และ `/api/ps` ว่าง คำขอสำเร็จและ record แรกมาภายใน 30 วินาที | โมเดลถูก unload แล้ว; **record แรก 4,144 ms** (ทดสอบแยกด้วยสคริปต์ Python ได้ header และ record แรกที่ 3,955 ms พร้อมกัน) |

**Header HTTP มาพร้อม record แรกเสมอ** (วัดทั้ง warm และ cold) แปลว่าในช่วง cold start Backend ไม่ได้รับข้อมูลใดจาก Ollama เลยราว 4–5 วินาที

### S14 Interrupted stream (เสริม `-IncludeRunnerStop`, failure injection), 5 checks

| Expected | Actual |
| --- | --- |
| สตรีมจบด้วย `{"error":…}` ไม่มี `done:true`, outcome `provider_error` / `AI_PROVIDER_ERROR` | ตรงทุกข้อ: หยุด runner หลัง record ที่ 15 สตรีมจบที่ record ที่ 18–19 ด้วย error record แล้วปิดปกติ (clean EOF) |
| ข้อความ upstream ถูกทำความสะอาด (ไม่มี endpoint) | `an error was encountered while running the model: read tcp <loopback>-><loopback>: wsarecv: An existing connection was forcibly closed by the remote host.` (ต้นฉบับมีพอร์ตของเครื่อง) |
| Ollama ให้บริการคำขอถัดไปได้ | สำเร็จ แต่ใช้ ≈ 5.3 วินาที (โหลดโมเดลใหม่) |

## เทสต์ด้วย mock (ไม่ต้องใช้โมเดล): 27 tests

`tests/test_ollama_streaming.py` ครอบคลุม:

- ตัวถอด `answer`: fragment จริง 2 ชุดที่จับจาก qwen3:4b (ชื่อ key ถูกแบ่ง, escape ถูกแบ่งข้าม record, ภาษาไทย), surrogate pair ที่ถูกแบ่ง, escape ครบชุด, ช่องว่าง, ทีละอักขระ, และ 10 รูปแบบที่ต้องปฏิเสธพร้อมเหตุผล
- ตัวอ่านสตรีม: บรรทัดและ UTF-8 (ไทย 3 ไบต์ และ emoji 4 ไบต์) ถูกตัดข้าม network read, field `thinking`, field ที่ไม่รู้จัก, สตรีมจบก่อน `done`, connection ถูก reset, record `error` พร้อมการทำความสะอาดข้อความ, 13 รูปแบบ `invalid`, `length`, HTTP error 5 รูปแบบ, ต่อไม่ได้, idle timeout, total timeout (ตรวจว่าเซิร์ฟเวอร์รู้ว่า client ปิด connection), การยกเลิกเทียบ drain ใน process เดียว, อนุญาตเฉพาะ loopback, การแมปรหัส error
- สคริปต์เต็มกับเซิร์ฟเวอร์จำลองที่ทำตาม contract: ผ่านทั้ง 13 scenarios; **เชิงลบ**: เซิร์ฟเวอร์ที่คืน `<think>` ใน answer ทำให้ S01 ล้มและ exit 1, เซิร์ฟเวอร์ที่ทำงานต่อหลัง client ตัด connection ทำให้ S10 ล้มและ exit 1; และ Ollama ไม่ทำงาน / URL ไม่ปลอดภัย / ที่เก็บหลักฐานไม่มี ทำให้ exit 1 ก่อนส่งคำขอใด

ระหว่างทำพบและแก้ข้อผิดพลาดของผมเองที่เกี่ยวกับความน่าเชื่อถือของผล: (1) scope ของตัวแปรใน scriptblock ทำให้การตัดสินผลหายไป (พบจากการทบทวนโค้ด ก่อนรันจริง) (2) เทสต์การยกเลิกเดิมวัดผิดเพราะ process ที่ปิดตัวฆ่า drain ที่ทำงานเบื้องหลัง จึงเปลี่ยนเป็นทำทั้งสองขั้นใน process เดียว

## ข้อจำกัด

- เครื่องเดียว (RTX 4050 6 GB) Ollama 0.40.5 โมเดลเดียว ตัวอย่างน้อย (S13 มี 5 คำขอ) ค่าเวลาใช้บอกลำดับความสำคัญ ไม่ใช่ SLA
- ผ่านทุก check ไม่รับประกันว่าโมเดลตอบถูกต้อง prompt ของ scenario เป็นคำถามสั้นที่ตรวจได้ (ขอบเขตเดียวกับ AI-003)
- S14 จำลองความล้มเหลวด้วยการหยุด runner ได้ error record ที่ Ollama จัดการเอง ไม่ใช่การตัด TCP ดิบหรือ Ollama ล่มทั้งตัว (กรณีหลังจำลองด้วย mock)
- ไม่ได้ทดสอบหลายสตรีมพร้อมกัน, reverse proxy, หรือเวอร์ชัน Ollama อื่น
- ทดสอบการยกเลิกเฉพาะกับ client แบบ `HttpClient` ของ .NET (ผ่าน PowerShell 7) ไม่ได้ทดสอบ ASP.NET `RequestAborted` ตรง ๆ (ตัว Backend เป็นขอบเขตของ BE-004)
- ไม่มีข้อมูลส่วนตัว password token หรือ secret ในหลักฐาน (พอร์ตของเครื่องในข้อความ error ถูกทำความสะอาดแล้ว)
