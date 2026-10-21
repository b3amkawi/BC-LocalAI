# FE-004 Evidence and Handoff

Issue: #45 · Branch `watzapon_FE-004` (แยกจาก `watzapon_BE-008` f1804e9 เพราะต้องใช้ contract ของ BE-008 ที่ยังไม่อยู่ใน `main`) · วันที่ 2026-10-21

## 1. สิ่งที่เปลี่ยน

| ไฟล์ | การเปลี่ยน |
|---|---|
| `wwwroot/js/chat-stream.js` | สถานะใหม่ `accepted` / `queued` / `processing` (`started` = **accepted** เท่านั้น ไม่ใช่ streaming); parser event `queued {ahead}` และ `processing`; `retryAfterSeconds` จาก error event และจาก header `Retry-After` (รับเฉพาะจำนวนเต็มวินาที 1–3600); `isActive()` ให้ Stop/ปุ่มทำงานทุกสถานะที่กำลังรอ |
| `wwwroot/js/chat-core.js` | ข้อความ `AI_BUSY` (503) และ `AI_REQUEST_IN_PROGRESS` (429) แยกจาก `AI_TIMEOUT`/`AI_UNAVAILABLE`; `describeFailure()` ต่อคำแนะนำรอเฉพาะเมื่อมี Retry-After ที่ถูกต้อง (ไม่มีนับถอยหลัง ไม่มี retry อัตโนมัติ) |
| `wwwroot/js/chat.js` | แสดงสถานะรอตามจริง, Stop ใช้ได้ตั้งแต่ connecting/queued/processing, `ahead` แสดงเป็น "ตอนรับคำถามมี N คำถามรออยู่ข้างหน้า" (snapshot ไม่ใช่ลำดับสด ไม่มี ETA) |
| `wwwroot/js/authenticated-chat.js`, `frontend-http.js` | หน้าที่ล็อกอินใช้ข้อความ/สถานะชุดเดียวกัน; `AI_REQUEST_IN_PROGRESS` อยู่ในรายการรหัสที่รับ; fault จาก 429/503 พก `retryAfterSeconds` |
| `tests/ui/chat-queue.test.mjs` (ใหม่ 21 ตัว), `chat-stream.test.mjs` (ปรับ 3 ตัว) | ดูข้อ 2 |

ไม่แตะ server (BE-008), config ของ Ollama, auth/history หรือ CSRF; ข้อความทั้งหมดยังเขียนด้วย `textContent`

**การเปลี่ยนพฤติกรรมที่ตั้งใจ:** หลัง `started` สถานะคือ "ได้รับคำถามแล้ว กำลังรอ AI เริ่มตอบ…" (เดิมเป็น "AI กำลังพิมพ์คำตอบ…") จนกว่าจะมี `processing` หรือ `delta` แรก ตามข้อกำหนดว่า `started` ไม่แปลว่าโมเดลเริ่มทำงาน test เดิม 3 ตัวที่ยืนยันสถานะ `streaming` หลัง `started` จึงถูกปรับเป็น `accepted`

## 2. ผลทดสอบอัตโนมัติ (mock จาก contract ฉบับร่างของ BE-008)

- `node --test tests/ui/*.test.mjs`: **198 ผ่าน / 0 ล้ม** (เดิม 177)
- ครอบคลุม: queued→processing→delta→completed, Stop ตอน queued/processing, error หลัง SSE (`AI_BUSY` + `retryAfterSeconds`), 429/503 ก่อน SSE, Retry-After มี / ไม่มี / วันที่ / 0 / ลบ / ทศนิยม / ใหญ่เกิน / ข้อความ, `ahead` ผิดรูปแบบไม่ถูกแสดง, ลำดับ event ผิด ⇒ `AI_INVALID_RESPONSE`, ข้อความบางส่วนคงอยู่และไม่เป็น completed, server เก่าที่ไม่ส่ง queue event ยังทำงาน, การหลุดขณะรอคิว, markup ใน error ไม่รั่ว, transport ของหน้าที่ล็อกอิน
- การกดส่งซ้ำ: guard `active` เดิมของ `chat.js`/`busy` ของหน้าล็อกอินยังอยู่ (ไม่มีการส่งซ้ำอัตโนมัติ) — ตรวจจากโค้ด ไม่มี test DOM ใหม่

## 3. หลักฐาน browser กับ Backend + Ollama จริง (เครื่องพัฒนา, `qwen3:4b`, คำถามสังเคราะห์, `Authentication:Enabled=false`)

ตั้ง `ChatQueue__MaxQueueLength=2`, `EmitQueueEvents=true`; ใช้คำขอ curl เรียงความยาวถือ slot ของ Ollama ไว้ (~13 s) เพื่อให้เกิดการรอ

| สถานการณ์ | Expected | Actual | ภาพ |
|---|---|---|---|
| คำขอที่สองระหว่างมีงานยาว (queue events เปิด) | แสดงสถานะรอคิว ไม่ใช่ "กำลังพิมพ์" มีปุ่มหยุด; ต่อมาตอบสำเร็จ | แสดง "ระบบมีผู้ใช้หลายคน … อยู่ในคิว … (ตอนรับคำถามมี 1 คำถามรออยู่ข้างหน้า)" และปุ่ม "หยุดสร้างคำตอบ"; ภายหลังได้คำตอบ "2" และกลับสู่สถานะพร้อมใช้งาน | [desktop-queued](FE-004/desktop-queued.jpg) |
| คิวเต็ม (1 ทำ + 2 รอ) | 503 ข้อความ "ระบบไม่ว่าง" + แนะนำ 10 วินาที; คำถามคืนช่องกรอก | ตรงตามนั้น (ไม่ใช่ข้อความ timeout/Ollama) | [desktop-busy-503](FE-004/desktop-busy-503.jpg) |
| กด Esc ระหว่างรอคิว | หยุด; ข้อความ "หยุดการสร้างคำตอบแล้ว"; ส่งใหม่ได้ | ตรงตามนั้น | [desktop-stop-while-queued](FE-004/desktop-stop-while-queued.jpg) |
| ผู้ใช้มีคำขอค้างอยู่ (โควตา 1, ค่าเริ่มต้น) | 429 ข้อความ "มีคำถามที่กำลังตอบอยู่ … รอหรือหยุดคำถามเดิม" ต่างจาก 503 | ตรงตามนั้น | [desktop-429](FE-004/desktop-429.jpg) |
| หน้าจอเล็ก 375×812 | ข้อความ 429 ห่อบรรทัดอ่านได้ ไม่ล้นจอ | ตรงตามนั้น | [mobile-429](FE-004/mobile-429.jpg) |
| ไทย + Emoji บนหน้าจอเล็ก | แสดงถูกต้อง | ตรงตามนั้น | [mobile-thai-emoji](FE-004/mobile-thai-emoji.jpg) |

**ไม่ได้ตรวจ / ข้อจำกัดของหลักฐาน:**
- ไม่ได้จับภาพสถานะ `processing` (ผ่านเร็วเกินไป) และไม่ได้ทดสอบ screen reader/keyboard-only อย่างเป็นทางการ (สถานะอยู่ใน `role="status"` เดิมและเป็นข้อความ ไม่พึ่งสีอย่างเดียว)
- หน้าที่ล็อกอิน (`authenticated-chat.js`) ทดสอบด้วย unit test ไม่ได้ลองใน browser (ต้องมี MySQL/ล็อกอิน)
- หลักฐานใช้ contract ฉบับร่างของ BE-008 (รหัส/event ยังรออนุมัติ) และตัวเลขคิวเป็นค่าทดลอง ไม่ใช่ความจุที่อนุมัติ
- "ตอนรับคำถามมี 1 คำถามรออยู่ข้างหน้า" เป็นค่า `ahead` จริงจาก backend ณ ตอนรับคำขอ ไม่ได้อัปเดตสด; ไม่มีการแสดงเวลารอ

## 4. Handoff ให้ QA

1. เปิด `ChatQueue__EmitQueueEvents=true` เพื่อเห็นสถานะ queued/processing; ถ้าปิด จะเห็น "ได้รับคำถามแล้ว…" จนมี delta แรก
2. สร้างการรอด้วยคำขอยาวหนึ่งคำขอแล้วส่งคำถามจากหน้าเว็บ; ตั้ง `ChatQueue__MaxPendingPerUser=16` เพื่อให้ถึงเส้นทาง 503 (โหมด auth ปิดใช้โควตาร่วมจึงเจอ 429 ก่อน)
3. เคสที่ควรทดสอบเพิ่ม: session หมดอายุระหว่างรอ, Retry-After ผิดรูปแบบจากพร็อกซี, ปิดแท็บระหว่างรอ, ล็อกอินหลายผู้ใช้พร้อมกัน
4. ยังต้องรออนุมัติ contract (รหัส `AI_BUSY`/`AI_REQUEST_IN_PROGRESS`, event `queued`/`processing`, field `retryAfterSeconds`) ตาม [BE-008-CONFIGURATION](BE-008-CONFIGURATION.md)

## 5. ทำซ้ำ

```powershell
node --test "tests/ui/*.test.mjs"
$env:ChatQueue__MaxQueueLength="2"; $env:ChatQueue__EmitQueueEvents="true"; dotnet run --project BC-LocalAI --urls http://127.0.0.1:5291
```
