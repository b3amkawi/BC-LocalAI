# BE-004: ผลรีวิวและแก้ไขก่อน push

ตรวจตาม Issue #17 และ `docs/ai/ollama-streaming-contract.md` บน branch `watzapon_BE-004`
ฐานงานเดิมคือ commit `82f7f43`; `origin/main` ที่ตรวจเมื่อเริ่มรอบนี้ยังเป็น `0cff336`

## ข้อบกพร่องที่พบและแก้แล้ว

| ประเด็น | ผลกระทบก่อนแก้ | การแก้และหลักฐาน |
|---|---|---|
| Streaming HttpClient ตาม redirect และใช้ proxy ค่าเริ่มต้น | คำขออาจออกจาก loopback ที่ผ่าน validation | ปิดทั้งสองค่า; `StreamingTransportTests.Redirect_IsNeverFollowed` ใช้ provider ผ่าน TCP และยืนยัน trap endpoint ไม่ถูกเรียก |
| รับ HTTP 2xx ทุกค่าและไม่ตรวจ Content-Type/charset | ตอบ completed ได้ทั้งที่ผิด AI-005 contract | รับเฉพาะ HTTP 200 + NDJSON UTF-8; regression tests ปฏิเสธ 201/206, HTML/plain text และ charset อื่น |
| JSON whitespace และ control characters ใช้กฎกว้าง/แคบผิด | รับ NBSP/vertical tab นอก string และปฏิเสธ DEL ที่ JSON อนุญาต | ใช้ whitespace 4 ตัวตาม JSON และห้ามเฉพาะ raw characters ต่ำกว่า U+0020 ใน string |
| ตรวจ record ไม่ครบและบังคับ response แม้ไม่ได้ส่ง | ปฏิเสธ final record ที่ถูก contract และรับ duplicate fields | แยก missing จาก explicit null; ตรวจชนิด known fields และ duplicate keys |
| Log เก็บ raw exceptions | ข้อความภายใน/credential อาจติด log | log เฉพาะ bounded code/reason/time หรือ exception type; sentinel tests ตรวจทั้ง message และ Exception object |
| Downstream ล้มเหลวระหว่างเขียน SSE | อาจพยายามส่ง error เพิ่มหลัง terminal ที่เขียนค้าง | รวม frame ต่อ write, จัดการ IOException/cancellation, หยุด upstream และไม่ส่ง terminal ซ้ำ |
| cancelled ไม่ถูกส่งแม้ response ยังเขียนได้ | ไม่ครบ event contract ของ BE-004 | ส่ง best-effort ด้วย delivery token แยก 1 วินาที; ทดสอบ writable cancellation |
| Flush ทุกตัวอักษร | เพิ่ม event/flush ตามจำนวนตัวอักษรโดยไม่จำเป็น | รวมข้อความต่อ upstream fragment; test ข้อความ 1,000 ตัวอักษรเหลือ 2 deltas โดยได้คำตอบเดิม |
| Concurrency test เดิมอาจทำงานเรียงกัน | ยังพิสูจน์ state isolation ไม่พอ | ใช้ barrier ให้ทั้งสองคำขออยู่ระหว่างทำงานจริงก่อนปล่อยให้จบ |

## กระบวนการทดสอบซ้ำ

เพิ่ม regression tests ก่อนแก้: พบ **10 failed / 14 passed** ในชุด service tests ซึ่งยืนยัน protocol, JSON และ logging defects จริง
หลังแก้และเพิ่ม controller/transport/Unicode/resource tests ชุด focused ผ่าน แล้วรัน full suite ตาม verification report

รอบตรวจพบ Python suite เดิมล้มเหลวต่างกรณีระหว่างรันเต็มสองครั้ง:

1. `test_cold_start_note_is_recorded_without_warmup_requests`: การส่งคำขอไป fake HTTP server ล้มเหลว 1/160; ทดสอบกรณีเดียวซ้ำผ่าน
2. `test_success_replaces_previous_evidence`: Windows `File.Replace` รายงาน `Unable to remove the file to be replaced`; ทดสอบกรณีเดียวซ้ำผ่าน

ทั้งสองกรณีไม่ได้รันโค้ด C# ของ BE-004 และยังระบุสาเหตุของ host-level transient ได้ไม่แน่นอน จึงไม่เปลี่ยนสคริปต์/เทสต์ของทีม AI
ผล rerun เต็มชุดบันทึกตามจริงใน verification report โดยไม่ลบประวัติรอบที่ล้มเหลว

## หลักฐานระบบจริง

- [ผล Streaming กับ Qwen จริง](be004/live-stream-review.json): ไทย อังกฤษ Emoji/multiline, concurrent arithmetic, cancellation, recovery และ API เดิม
- [ผล output limit=16](be004/live-output-limit.json): `started`, deltas, `error AI_RESPONSE_TOO_LARGE` และ `partial:true` โดยไม่มี completed
- [หน้าเว็บตอบภาษาไทย](be004/browser-thai.png): ตรวจ regression ของ FE-001 ซึ่งยังใช้ `/api/chat`
- ใช้ Ollama **0.40.6**, `qwen3:4b`, Intel i7-14700HX และ RTX 4060 Laptop 8 GB บนเครื่องทดสอบ
- สเปกอ้างอิงของโปรเจกต์ยังเป็น ASUS TUF Gaming F16 / Core 5 210H / RAM 16 GB / RTX 4050 Laptop 6 GB
  ผลเวลาบนเครื่องทดสอบไม่ใช่ SLA ของสเปกอ้างอิง; ค่าประมาณจาก baseline เดิมคือช้าลงราว 25% (ไม่ได้วัดซ้ำบนสเปกอ้างอิงรอบนี้)

## ขอบเขตที่ยังไม่ได้ตรวจ

Reverse proxy ภายนอก, หลายเครื่อง และ production deployment ไม่ได้ทดสอบในรอบนี้
FE-002, authentication และ chat history ยังเป็นงาน issue แยก; ข้อเสนอ first-record/idle timeout ยังใช้ total deadline เดิม 180 วินาที
รายงานนี้เป็น self-review และผลทดสอบ ไม่ใช่การอนุมัติจากผู้รีวิวอิสระ
