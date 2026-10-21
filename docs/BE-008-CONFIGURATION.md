# BE-008: Bounded Request Queue and Per-User Concurrency Limits

สถานะ: **implementation พร้อม review** · ค่าเริ่มต้นและรหัส error/event ใหม่ทั้งหมดเป็น **ข้อเสนอ ยังไม่ได้รับอนุมัติ** (ดูข้อ 5)

## 1. พฤติกรรมโดยสรุป

ทุกคำขอไป `/api/chat` และ `/api/chat/stream` ผ่านประตูเดียวกัน (`ChatAdmissionQueue`, Singleton) หลังผ่าน Authentication/Authorization แล้วเท่านั้น

```
ผ่าน [Authorize] ──► ตรวจ message ──► Admit(ผู้ใช้) ──┬─ โควตาผู้ใช้เต็ม ──► 429 AI_REQUEST_IN_PROGRESS
                                                      ├─ คิวรวมเต็ม ──────► 503 AI_BUSY
                                                      └─ รับแล้ว ─► (มี slot ว่าง: ไป Ollama ทันที)
                                                                    (ไม่ว่าง: รอคิว FIFO ≤ MaxQueueWaitSeconds)
```

- ปฏิเสธ 429/503 ที่ `Admit` ซึ่งเกิด **ก่อนบันทึกคำถามลงประวัติ ก่อนเปิด SSE และก่อนแตะ GPU** จึงเป็น HTTP status ปกติ
- คำขอที่ต้องรอ: `/api/chat` ค้างรอ response ตามเดิม; `/api/chat/stream` เปิด SSE แล้วส่ง `started` ตามเดิม แล้วรอ (`started` แปลว่า "รับคำขอแล้ว" ไม่ได้แปลว่าโมเดลเริ่มทำงาน)
- รอเกิน `MaxQueueWaitSeconds`: non-stream ได้ HTTP 503 `AI_BUSY`; stream ได้ `error` event `AI_BUSY` (status เปลี่ยนย้อนหลังไม่ได้แล้ว) — **แยกจาก `504 AI_TIMEOUT`** ซึ่งเป็นเวลา generate และเริ่มนับเมื่อได้ slot เท่านั้น
- ผู้ใช้ปิดหน้า/ยกเลิกระหว่างรอ: ออกจากคิวทันที ไม่ส่งต่อ Ollama ไม่นับเป็น AI failure; ระหว่าง generate: token ยกเลิกถึง Ollama ตามเดิม แล้ว slot ถูกคืนให้คนถัดไป
- slot และโควตาคืนในทุกเส้นทาง (สำเร็จ, error, timeout, ปิดการเชื่อมต่อ, ยกเลิก) ด้วย `ChatTicket.Dispose` ที่ idempotent; ใน stream คืน **ก่อน** ส่ง event สุดท้าย เพื่อให้คำถามถัดไปที่ส่งทันทีที่เห็น `completed` ไม่ถูก 429

## 2. Configuration (`ChatQueue`)

| Key | ค่าเริ่มต้น | ช่วงที่ validate ตอน startup | เหตุผลของค่าเริ่มต้น |
|---|---:|---|---|
| `MaxConcurrentRequests` | 1 | 1–16 | Ollama ค่าเริ่มต้น `NUM_PARALLEL=1`; AI-006 (`docs/performance/ai-006-report.md` ข้อ 4) ยังไม่เสนอเปลี่ยน ถ้า Leader/เติ้ลเปิด `NUM_PARALLEL=2` ให้แก้สองค่านี้พร้อมกัน |
| `MaxQueueLength` | 8 | 0–256 | **ข้อเสนอ** AI-007/AI-006 ข้อ 6: รอ ≈ 30 วินาทีหารเวลาให้บริการ (medium ≈ 1.3 s, long ≈ 3 s) ได้ ≈ 9–21 คำขอ เลือก 8 ให้ long รอสูงสุด ≈ 24 s ที่ NP=1; `0` = ไม่มีคิว (ไม่ว่างแล้วปฏิเสธทันที) |
| `MaxQueueWaitSeconds` | 30 | 1–300 | เวลารอที่ผู้ใช้ยอมรับได้ตาม AI-007; ต่ำกว่า `Ollama:TimeoutSeconds` (180) มากโดยตั้งใจ |
| `MaxPendingPerUser` | 1 | 1–16 | **ข้อเสนอ** AI-007 ข้อ 4: 1 คำขอค้างต่อผู้ใช้ (นับทั้งที่กำลังทำและที่รอคิว) |
| `RetryAfterSeconds` | 10 | 1–300 | ค่าคงที่เป็น *คำแนะนำ* ใน `Retry-After` — ไม่ใช่การพยากรณ์ว่าเมื่อใด slot จะว่าง (ไม่มีข้อมูลที่เชื่อถือได้) |
| `EmitQueueEvents` | false | — | เปิด event `queued`/`processing` เฉพาะเมื่อ FE-004 และเจ้าของยอมรับสัญญา (ข้อ 4) |

ค่าที่ไม่ถูกต้องทำให้แอปไม่เริ่มทำงาน (`ValidateOnStart`) ข้อความระบุชื่อ key โดยไม่พิมพ์ค่า ปรับผ่าน `appsettings` หรือ environment เช่น `ChatQueue__MaxQueueLength=12`

ค่า 8 และ 1 **ไม่ใช่ความจุที่อนุมัติแล้ว** และไม่ได้รับประกันจำนวนผู้ใช้ — ต้องยืนยันด้วย AI-006 บนเครื่องเป้าหมายและโหลดจริง (AI-012 ยังไม่ได้สร้างในงานนี้)

## 3. Identity และโหมดทดสอบ

- โควตานับตาม `ClaimTypes.NameIdentifier` ของ principal ที่ cookie/session ที่ Backend ตรวจแล้วเท่านั้น; ไม่อ่าน `userId` ใน body, header หรือ query ทุกชนิด (มี test ว่าการปลอมไม่ช่วยเลี่ยงโควตา)
- เมื่อ `Authentication:Enabled=false` (โหมดพัฒนา/ทดสอบ ไม่มีตัวตน) ทุกคำขอใช้ key เดียวกันคือ `anonymous` ⇒ **โควตา 1 คำขอค้างใช้ร่วมกันทั้งระบบ** เข้มงวดกว่าโหมดจริง และไม่มีทางเลี่ยง ถ้าต้องยิงหลายคำขอพร้อมกันในโหมดนี้ (เช่น benchmark, QA harness) ให้ตั้ง `ChatQueue__MaxPendingPerUser` สูงขึ้นอย่างชัดเจน ห้ามใช้ในโหมดที่มีผู้ใช้จริง
- ผู้ไม่มีสิทธิ์ถูก 401/403 โดย `[Authorize]` ก่อนถึง `Admit` (มี test: ไม่ล็อกอินแล้วไม่มี admitted/rejected/เรียกโมเดลเลย)
- ใช้ in-memory ในโปรเซสเดียว: ถ้ารัน Backend หลายอินสแตนซ์ ขีดจำกัดจะนับแยกกัน (นอกขอบเขต — ไม่มี distributed queue)

## 4. Contract ที่เสนอ (ต้องยืนยันก่อนใช้จริง: เจ้าของ Backend/UI + Leader)

### HTTP (ก่อนเปิด SSE และ `/api/chat` ทั้งหมด)

| กรณี | Status | `error.code` | Header | ข้อความ |
|---|---:|---|---|---|
| ผู้ใช้มีคำขอค้างเกิน `MaxPendingPerUser` | **429** | `AI_REQUEST_IN_PROGRESS` | `Retry-After: <RetryAfterSeconds>` | คุณมีคำถามที่กำลังตอบอยู่ กรุณารอให้เสร็จก่อนถามคำถามถัดไป |
| คิวรวมเต็ม | **503** | `AI_BUSY` | `Retry-After` | ตอนนี้มีผู้ใช้จำนวนมาก กรุณาลองใหม่ในอีกสักครู่ |
| รอคิวเกินเวลา (`/api/chat`) | **503** | `AI_BUSY` | `Retry-After` | เหมือนข้างบน |

โครง body ยังเป็น envelope เดิม `{ "error": { "code", "message" } }` ข้อความตรงกับตารางใน `docs/ai/ai-failure-policy.md` ข้อ 5 รหัส `AI_REQUEST_IN_PROGRESS` เป็นชื่อที่ผู้ implement เลือกเพื่อเสนอ (AI-007 กำหนดเฉพาะ status) — เปลี่ยนได้ที่ `AiServiceException.RequestInProgress` จุดเดียว

### SSE (หลังเปิดสตรีมแล้ว)

- รอคิวเกินเวลา: `error` event ปกติ `{"code":"AI_BUSY","message":"...","partial":false,"retryAfterSeconds":10}` เป็น event สุดท้าย ไม่มี `delta`/`completed` ตามมา (field `retryAfterSeconds` เป็น field **ใหม่แบบ optional** ที่ปรากฏเฉพาะ `AI_BUSY` เพราะส่ง header ไม่ได้แล้ว; client เดิมที่ไม่อ่าน field นี้ไม่กระทบ)
- ถ้า `EmitQueueEvents=true` และคำขอต้องรอ: `started` → `queued` `{"ahead":N}` → (ได้ slot) `processing` `{}` → `delta`… → terminal คำขอที่ได้ slot ทันทีจะไม่ได้รับ `queued`/`processing`
- `ahead` คือจำนวนคำขอที่รออยู่ข้างหน้า **ณ ตอนรับคำขอ** (snapshot) ไม่อัปเดตและไม่ใช่เวลารอ — **ไม่มีการประมาณเวลารอ** เพราะยังไม่มีข้อมูลที่เชื่อถือได้ (UI ต้องไม่แสดงเป็นเวลา) ตำแหน่งแบบอัปเดตสดไม่รองรับในงานนี้
- ถ้า `EmitQueueEvents=false` (ค่าเริ่มต้น) สตรีมเหมือนก่อน BE-008 ทุกประการ ยกเว้น error `AI_BUSY` ข้างบน

## 5. สิ่งที่ยังรออนุมัติ / ไม่ได้ตัดสินใจ

1. รหัส `AI_BUSY` / `AI_REQUEST_IN_PROGRESS`, `Retry-After`, field `retryAfterSeconds` และ event `queued`/`processing` — รอ FE-004 #45 + เจ้าของ Backend/UI + Leader ก่อนเปิดใช้บนระบบจริง
2. ตัวเลข 1 / 8 / 30 s / 1 ต่อผู้ใช้ — เป็นข้อเสนอ ต้องยืนยันกับ AI-006 และเครื่องเป้าหมาย
3. การใช้ `NUM_PARALLEL=2` — การตัดสินใจของ Leader/เติ้ล (นอกขอบเขตงานนี้)

## 6. Metrics และ log

- Meter `BC-LocalAI.ChatQueue` (System.Diagnostics.Metrics ไม่มี dependency เพิ่ม): gauge `bc.chat.queue.active`, `bc.chat.queue.waiting`; counter `bc.chat.queue.admitted`, `bc.chat.queue.rejected{reason=user-quota|queue-full}`, `bc.chat.queue.cancelled`, `bc.chat.queue.wait_timeout`; histogram `bc.chat.queue.wait` (วินาที) — สำหรับ OPS-002
- "cancelled" นับเฉพาะคำขอที่ออกจากคิวเพราะผู้ใช้ไป; การยกเลิกระหว่าง generate ถูกบันทึกโดย log เดิมของ `OllamaAIService`
- Log 4101 (refused: reason, active, queued) และ 4102 (รอนานเกิน 5 s: ms) มีเฉพาะตัวเลขและหมวดคงที่ — ไม่มีคำถาม คำตอบ cookie token หรือ user id (มี test ว่า response ไม่มีคำถาม)

## 7. ข้อจำกัด

- ตำแหน่งในคิวไม่ถูกอัปเดตหลังรับคำขอ และไม่มีเวลารอโดยประมาณ
- คิวอยู่ใน memory ของโปรเซส: restart ทำให้คำขอที่รอหายไป (client ได้การเชื่อมต่อขาด)
- FIFO อย่างเดียว ไม่มี priority ตาม role (นอกขอบเขต)
- ค่าความจุยังไม่ผ่านการวัดโหลดบนเครื่องเป้าหมาย (AI-012) และไม่รวมผลวัด live กับผู้ใช้หลายคนจริง

## 8. Handoff

**FE-004 (#45):** รับ 429/503 + `Retry-After` เป็น error ที่แสดงข้อความตามข้อ 4; รองรับ `error` ที่มี `code=AI_BUSY` กลาง/ต้นสตรีม (`partial=false`); `queued.ahead` ใช้บอก "มีคนรออยู่ข้างหน้า" ได้ แต่ห้ามแปลงเป็นเวลา; ห้ามถือว่า `started` = โมเดลกำลังตอบ (ใช้ `processing` เมื่อเปิดแล้ว หรือ `delta` แรก) ปุ่มลองใหม่ควรหน่วงตาม `Retry-After`

**AI-012 (โหลดเทสต์ ยังไม่สร้าง):** ใช้ `scripts/test-chat-queue.py` เป็นจุดเริ่ม; ในโหมด auth ปิดต้องตั้ง `ChatQueue__MaxPendingPerUser` ให้สูงพอ มิฉะนั้นทุกคำขอที่สองจะได้ 429; อ่าน meter `BC-LocalAI.ChatQueue` เพื่อเทียบเวลารอคิวกับเวลา generate
