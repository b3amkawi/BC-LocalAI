# QA-001 Test Plan and Case List (D1)

แผนทดสอบเส้นทาง streaming ตั้งแต่ผู้ใช้ถึง Ollama: SSE client → `POST /api/chat/stream` (Backend ที่ build จริง ใช้ Production) → Ollama
Expected ของทุกกรณีถูกเขียนก่อนรัน (ในโค้ดของชุดทดสอบ) และ Actual บันทึกแยกใน evidence ไม่ปรับ Expected หลังเห็นผลโดยไม่บันทึก

## ขอบเขตและวิธีทดสอบ

| ชุด | วิธี | ใช้ทดสอบอะไร | ไฟล์ |
| --- | --- | --- | --- |
| Mock | Backend จริงต่อกับ Ollama จำลองบน loopback ที่สั่งพฤติกรรมได้ | ความผิดพลาดที่ทำซ้ำบนโมเดลจริงยาก: runner ล้ม, หยุดนิ่ง, record เสีย, ข้อมูลเกินขนาด, คิว | `tests/e2e/streaming_e2e.py` (74 กรณี รันกรณีละ 2 ครั้ง) |
| Live | Backend จริงต่อกับ Ollama และ qwen3:4b จริง | สถานการณ์ปกติ, cold start, การยกเลิก, หลายคำขอ, `/api/chat` เดิม ตรวจเชิงโครงสร้างเพราะคำตอบโมเดลไม่คงที่ | `tests/e2e/live_streaming_e2e.py` (33 กรณี รันกรณีละ 2 ครั้ง) |
| UI | เบราว์เซอร์กับ FE-002 | ปุ่ม Stop, ปิดแท็บ, การแสดง HTML, สถานะรอคิว | **ยังทดสอบไม่ได้: FE-002 ยังไม่ merge** |

ทุกกรณีรันอย่างน้อย 2 ครั้ง และจำนวนกรณีเป็น 2 เท่าของรายการตัวอย่างใน Issue (แต่ละหัวข้อมีตัวแปรภาษาไทย/อังกฤษหรือรูปแบบสองแบบ)

กติการ่วม: ไม่มีข้อมูลส่วนตัว รหัสผ่าน หรือ token ในชุดคำถาม ใช้ canary string ตรวจว่า log ของ Backend ไม่มีข้อความผู้ใช้/คำตอบ ข้อบกพร่องด้านความปลอดภัยถือเป็น blocker

## กรณีทดสอบชุด Mock

### 3.1 สถานการณ์ปกติ

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-N-n_thai | ภาษาไทย (ตัวอักษรแบ่งกลางชิ้นข้อมูล) | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_thai_b | ภาษาไทยมีวรรณยุกต์ (ทีละอักขระ) | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_english | ภาษาอังกฤษ | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_english_b | ภาษาอังกฤษมีเครื่องหมายวรรคตอน | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_emoji | emoji (คู่ surrogate แบ่งทีละอักขระ) | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_emoji_escaped | emoji ที่มาเป็น \uXXXX | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_multiline | หลายบรรทัดและบรรทัดว่าง | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_multiline_b | CRLF และ tab | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_special | อักขระพิเศษ " < & \ | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_special_b | แท็ก HTML และ entity เป็นข้อความ | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_short | คำตอบสั้นมาก | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_short_b | คำตอบสั้น 2 ตัวอักษร | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_medium | คำตอบขนาดกลาง | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_medium_b | คำตอบขนาดกลางภาษาไทย | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_long | คำตอบยาว (ภาษาไทย) | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_long_b | คำตอบยาว (ภาษาอังกฤษ) | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_thinking_field | ฟิลด์ thinking ต้องไม่ถึงผู้ใช้ | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-n_thinking_field_b | ฟิลด์ thinking กับภาษาไทย | Backend | completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด |
| QA1-N-cold | cold start: started ถึงก่อน record แรกของ Ollama | Backend | started ≤ 1 วินาที ทั้งที่ Ollama ตอบช้า 2 วินาที |
| QA1-N-cold-b | cold start ภาษาไทย | Backend | completed และข้อความตรง |
| QA1-N-headers | header ของสตรีม | Backend | text/event-stream, no-cache, X-Accel-Buffering: no |
| QA1-N-validation | ข้อความว่าง/ไม่มี/ผิดชนิด | Backend | 400 INVALID_MESSAGE ก่อนเริ่มสตรีม |

### 3.2 สถานการณ์ผิดพลาด

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-E-e_down | Ollama ไม่ทำงาน | Backend | error ครั้งเดียว code=AI_UNAVAILABLE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_http500 | Ollama ตอบ HTTP 500 | Backend | error ครั้งเดียว code=AI_PROVIDER_ERROR partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_http500_b | Ollama ตอบ HTTP 500 (ซ้ำ) | Backend | error ครั้งเดียว code=AI_PROVIDER_ERROR partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_crash | runner ล้มก่อนมีข้อความถึงผู้ใช้ | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_crash_b | runner ล้มหลังส่งข้อความไปแล้ว | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_length | คำตอบถูกตัด (length) | Backend | error ครั้งเดียว code=AI_RESPONSE_TOO_LARGE partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_length_b | คำตอบถูกตัดภาษาไทย | Backend | error ครั้งเดียว code=AI_RESPONSE_TOO_LARGE partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_invalid_json | บรรทัดที่ไม่ใช่ JSON | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_invalid_json_b | บรรทัดที่ไม่ใช่ JSON (ซ้ำ) | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_wrong_model | โมเดลในตอบไม่ตรง | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_wrong_model_b | โมเดลไม่ตรง (ซ้ำ) | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_no_done | สตรีมจบโดยไม่มี done ก่อนมีข้อความ | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_no_done_b | สตรีมจบโดยไม่มี done หลังส่งข้อความ | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_error_record | record error ก่อนมีข้อความ (มีรายละเอียดภายใน) | Backend | error ครั้งเดียว code=AI_PROVIDER_ERROR partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_error_record_b | record error หลังส่งข้อความ | Backend | error ครั้งเดียว code=AI_PROVIDER_ERROR partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_bad_ct | Content-Type ไม่ใช่ ndjson | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_bad_ct_b | Content-Type ผิด (ซ้ำ) | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_oversize | ข้อมูลเกิน MaxResponseBytes | Backend | error ครั้งเดียว code=AI_RESPONSE_TOO_LARGE ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_oversize_b | ข้อมูลเกินขนาด (ซ้ำ) | Backend | error ครั้งเดียว code=AI_RESPONSE_TOO_LARGE ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_think_leak | <think> ในคำตอบ | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_think_leak_b | <think> ภาษาไทย | Backend | error ครั้งเดียว code=AI_INVALID_RESPONSE ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_first | timeout: รอ record แรก | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_first_b | timeout รอ record แรก (ซ้ำ) | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_gap | timeout: ช่องว่างก่อนมีข้อความ | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=false ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_gap_b | timeout: ช่องว่างหลังส่งข้อความ | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_total | timeout: เวลารวม (หยดช้า) | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-e_timeout_total_b | timeout เวลารวม (ซ้ำ) | Backend | error ครั้งเดียว code=AI_TIMEOUT partial=true ไม่มี completed ไม่มีรายละเอียดภายใน |
| QA1-E-queue-full | คิวเต็ม: คำขอที่รอเกิน timeout | Backend | คำขอที่สองได้ AI_TIMEOUT partial=false ไม่มีข้อความ |

### 3.3 การยกเลิก

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-C-first_delta | ตัด connection หลัง delta แรก | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |
| QA1-C-first_delta-b | ตัด connection หลัง delta แรก | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |
| QA1-C-started | ตัด connection หลัง started ระหว่างรอ Ollama | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |
| QA1-C-started-b | ตัด connection หลัง started ระหว่างรอ Ollama | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |
| QA1-C-0.8 | ตัด connection ที่ 0.8 วินาที | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |
| QA1-C-0.8-b | ตัด connection ที่ 0.8 วินาที | Backend | Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed |

### 3.4 ความปลอดภัยของการแสดงผล

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-X-x_script | คำตอบอันตราย: x_script | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-x_script_b | คำตอบอันตราย: x_script_b | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-x_inject | คำตอบอันตราย: x_inject | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-x_inject_b | คำตอบอันตราย: x_inject_b | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-x_control | คำตอบอันตราย: x_control | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-x_control_b | คำตอบอันตราย: x_control_b | Backend | ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event |
| QA1-X-csp | Content-Security-Policy ของหน้าแชต | Backend | CSP มี default/script/connect-src 'self', object-src 'none', ไม่มี unsafe-*, nosniff |

### 3.5 หลายคำขอพร้อมกัน

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-K-2 | 2 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |
| QA1-K-2-b | 2 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |
| QA1-K-3 | 3 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |
| QA1-K-3-b | 3 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |
| QA1-K-5 | 5 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |
| QA1-K-5-b | 5 คำขอพร้อมกัน | Backend | ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ |

### Regression ของ POST /api/chat

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-R-ok | POST /api/chat ปกติ | Backend | พฤติกรรมเดิมของ /api/chat ไม่เปลี่ยน |
| QA1-R-thai | POST /api/chat ภาษาไทย | Backend | พฤติกรรมเดิมของ /api/chat ไม่เปลี่ยน |
| QA1-R-empty | POST /api/chat ข้อความว่าง | Backend | พฤติกรรมเดิมของ /api/chat ไม่เปลี่ยน |
| QA1-R-provider_error | POST /api/chat เมื่อ Ollama ตอบ 500 | Backend | พฤติกรรมเดิมของ /api/chat ไม่เปลี่ยน |

### log ของ Backend

| ID | กรณี | เจ้าของ | Expected |
| --- | --- | --- | --- |
| QA1-L-log-privacy | log ของ Backend ไม่มีคำถาม คำตอบ หรือรายละเอียดภายในของ Ollama | Backend | ไม่พบ canary ของ prompt/answer/ข้อความ error ภายในใน log ทุก instance |

## กรณีทดสอบชุด Live

| ID | หัวข้อ | กรณี | Expected |
| --- | --- | --- | --- |
| L-N-thai-1 | 3.1 | ภาษาไทย 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง และคำตอบมีอักษรไทย |
| L-N-thai-2 | 3.1 | ภาษาไทย 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง และคำตอบมีอักษรไทย |
| L-N-english-1 | 3.1 | ภาษาอังกฤษ 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-english-2 | 3.1 | ภาษาอังกฤษ 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-emoji-1 | 3.1 | emoji ในคำถาม 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-emoji-2 | 3.1 | emoji ในคำถาม 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-multiline-1 | 3.1 | ข้อความหลายบรรทัด 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-multiline-2 | 3.1 | ข้อความหลายบรรทัด 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-special-1 | 3.1 | อักขระพิเศษ 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-special-2 | 3.1 | อักขระพิเศษ 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-short-1 | 3.1 | คำตอบสั้น 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-short-2 | 3.1 | คำตอบสั้น 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-medium-1 | 3.1 | คำตอบขนาดกลาง 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-medium-2 | 3.1 | คำตอบขนาดกลาง 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง |
| L-N-long-1 | 3.1 | คำตอบยาว 1 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง (หรือ error AI_RESPONSE_TOO_LARGE ถ้าเกินขีดจำกัด) |
| L-N-long-2 | 3.1 | คำตอบยาว 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง (หรือ error AI_RESPONSE_TOO_LARGE ถ้าเกินขีดจำกัด) |
| L-N-cold-1 | 3.1 | cold start 1: unload แล้วถาม | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง และ started มาก่อน record แรก |
| L-N-cold-2 | 3.1 | cold start 2 | completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง และ started มาก่อน record แรก |
| L-C-first-delta-1 | 3.3 | ตัด connection หลัง delta แรก (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-C-started-1 | 3.3 | ตัด connection หลัง started (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-C-1s-1 | 3.3 | ตัด connection ที่ 1 วินาที (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-C-first-delta-2 | 3.3 | ตัด connection หลัง delta แรก (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-C-started-2 | 3.3 | ตัด connection หลัง started (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-C-1s-2 | 3.3 | ตัด connection ที่ 1 วินาที (โมเดลจริง) | ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที |
| L-K-2-1 | 3.5 | 2 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-K-2-2 | 3.5 | 2 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-K-3-1 | 3.5 | 3 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-K-3-2 | 3.5 | 3 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-K-5-1 | 3.5 | 5 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-K-5-2 | 3.5 | 5 คำขอพร้อมกันบนโมเดลจริง | ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ |
| L-R-chat-1 | regression | POST /api/chat เดิมกับโมเดลจริง | 200 ตอบถูกรูปแบบ ไม่มี JSON ซ้อน |
| L-R-chat-2 | regression | POST /api/chat ภาษาไทยกับโมเดลจริง | 200 มีคำตอบ |
| L-L-log-privacy | 3.4 | log ของ Backend ไม่มีคำถามหรือคำตอบ | ไม่พบ canary ใน log |

## กรณีที่ยังทดสอบไม่ได้ (รอ FE-002)

| ID | หัวข้อ | กรณี | เจ้าของ | รอ |
| --- | --- | --- | --- | --- |
| QA1-U-stop | 3.3 | ปุ่ม Stop Generating | UI | FE-002 |
| QA1-U-close-tab | 3.3 | ปิดแท็บระหว่างสตรีม | UI | FE-002 |
| QA1-U-navigate | 3.3 | เปลี่ยนหน้าระหว่างสตรีม | UI | FE-002 |
| QA1-U-render-html | 3.4 | คำตอบที่มี HTML/script แสดงเป็นข้อความในหน้าแชต | UI | FE-002 |
| QA1-U-queue-indicator | 3.5 | UI แสดงสถานะรอคิว | UI | FE-002 |
| QA1-U-partial-display | 3.2 | UI แสดงข้อความบางส่วนก่อน error | UI | FE-002 |

Expected ของกรณี UI: Stop หยุดการสร้างและข้อความสถานะเป็น `cancelled`; ปิดแท็บ/เปลี่ยนหน้าทำให้ Ollama หยุดภายใน 1.5 วินาที; คำตอบที่มี `<script>` แสดงเป็นข้อความ; มีตัวบอกสถานะรอคิวเมื่อมีหลายคำขอ; ข้อความบางส่วนที่รับก่อน error แสดงพร้อมข้อความ error

## วิธีรัน

```bash
dotnet build BC-LocalAI.slnx
python tests/e2e/streaming_e2e.py --output docs/qa/qa-001-evidence.json
python tests/e2e/live_streaming_e2e.py --output docs/qa/qa-001-live-evidence.json   # ต้องมี Ollama + qwen3:4b
python tests/e2e/make_qa_docs.py
```
