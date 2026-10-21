# BE-008 Review Checklist

Issue: #44 · Branch: `watzapon_BE-008` (จาก `origin/main` e6ab412) · Owner/Reviewer: ตาม [AGENTS.md](../AGENTS.md) (merge โดย owner เท่านั้น)

## 1. Acceptance Criteria (Issue #44 ข้อ 7)

| # | เกณฑ์ | หลักฐาน | สถานะ |
|---|---|---|---|
| 1 | มีขีดจำกัด active/queued/per-user/queue-wait ตั้งค่าและ validate ได้ พร้อมเหตุผลค่าเริ่มต้น | `Configuration/ChatQueueOptions.cs`, `ChatQueueOptionsValidatorTests`, [CONFIGURATION ข้อ 2](BE-008-CONFIGURATION.md) | ทำแล้ว (ค่าเป็นข้อเสนอ) |
| 2 | เกินโควตา 429; คิวเต็ม 503 ตาม contract ที่ทดสอบแล้ว | `ChatAdmissionApiTests` (Chat/Stream × 429/503, `Retry-After`) | ทำแล้ว; **contract รออนุมัติ** |
| 3 | Authentication/authorization ก่อนใช้ทรัพยากร; identity ปลอมเลี่ยงโควตาไม่ได้ | `ChatAdmissionIdentityTests` (401 ไม่แตะคิว, ปลอม header/body ยังนับเป็นผู้ใช้เดิม, auth ปิด = โควตาร่วม) | ทำแล้ว |
| 4 | ไม่ส่งคำขอที่ยกเลิกในคิวไป Ollama; ยกเลิก upstream เมื่อเลิกระหว่างประมวลผล | `ClientLeavingWhileQueued_*`, `ClientLeavingWhileProcessing_*` | ทำแล้ว (mock) |
| 5 | คืน permit/โควตาครบทุกเส้นทาง ไม่มี deadlock/leak | `HeavyConcurrency_*`, `GrantRacingWithCancellation_*`, `DisposeTwice_*`, `ProviderError_ReleasesTheSlotAndQuota` | ทำแล้ว |
| 6 | `/api/chat` และ `/api/chat/stream` ใช้นโยบายเดียวกัน | `BothEndpoints_ShareOneCapacity`, `ManyParallelRequests_*` (สลับ 2 endpoint) | ทำแล้ว |
| 7 | queued/processing/completion/error/cancellation กำหนดและทดสอบ รวมก่อน/หลัง SSE headers | `Stream_FullQueue_IsAPlainHttp503_*` (ก่อน), `Stream_WaitingTooLong_*` (หลัง), `Stream_QueueEvents_*` | ทำแล้ว; event `queued`/`processing` **ปิดอยู่รออนุมัติ** |
| 8 | concurrency tests + หลักฐานรับงานใหม่หลัง error/timeout/cancel | ชุดข้างบน + ทุก test ลงท้ายด้วย `Snapshot()==(0,0)` และคำขอถัดไปสำเร็จ | ทำแล้ว |
| 9 | metrics/log ไม่มี secret หรือเนื้อหาสนทนา | `ChatQueueLog` (ตัวเลขและหมวดคงที่), `Logs_AndErrors_NeverContainTheQuestion` | ทำแล้ว (log: ตรวจโค้ด + test response) |
| 10 | มีผลทดสอบ ข้อจำกัด และ Handoff ให้ FE-004 | [evidence](BE-008-evidence.md), [CONFIGURATION ข้อ 7–8](BE-008-CONFIGURATION.md) | ทำแล้ว |

## 2. จุดที่ผู้ตรวจควรดูเป็นพิเศษ

1. `Services/ChatAdmission/ChatAdmissionQueue.cs` — ล็อกเดียว (`_gate`) ปกป้อง `_active`, `_waiting`, `_pendingPerUser`; ทุกการเปลี่ยน state ของ ticket ทำใต้ล็อก; การ complete `TaskCompletionSource` ทำนอกล็อก (`RunContinuationsAsynchronously`) — ตรวจ invariant "waiting ไม่ว่าง ⇒ active == max"
2. `ChatTicket.WaitAsync` — สาย cancel/timeout เรียก `Release` ก่อน throw; กรณี grant กับ cancel พร้อมกันคืน slot ทันที
3. `ChatController` / `ChatStreamController` — `Admit` อยู่หลัง `[Authorize]` และหลังตรวจ message, **ก่อน** `turns.BeginAsync` (คำถามที่ถูกปฏิเสธไม่ถูกบันทึกลงประวัติ); `Dispose` ก่อนบันทึกคำตอบ/ก่อน event สุดท้าย
4. `AiServiceException.Busy/RequestInProgress` + `ExceptionHandlingMiddleware` (ตั้ง `Retry-After`) — ไม่มีข้อความภายในรั่ว
5. การเลือกรหัส `AI_REQUEST_IN_PROGRESS` และ field `retryAfterSeconds` เป็นของผู้ implement เสนอ — ต้องตัดสินใจ

## 3. การเปลี่ยนที่กระทบของเดิม

- `Qa002StreamTests.OneUserFlooding_*` ตั้ง `ChatQueue:MaxPendingPerUser=16`/`MaxQueueLength=32` เพื่อคงเงื่อนไข baseline "ไม่มีเพดานต่อผู้ใช้" ที่ QA-002 บันทึกไว้ (QA2-D06) โดยไม่แก้ oracle/เอกสาร QA — **QA2-D06 ถูกแก้โดย BE-008** แต่เจ้าของ QA-002 ต้อง re-baseline case `QA2-Q-01/03` เอง (ไม่ได้ทำในงานนี้)
- ค่าเริ่มต้นเมื่อ `Authentication:Enabled=false`: ทุกคนใช้โควตาร่วม 1 คำขอค้าง — เครื่องมือที่ยิงพร้อมกันในโหมดนี้ (benchmark/QA harness) ต้องตั้ง `ChatQueue__MaxPendingPerUser`
- `Ollama:TimeoutSeconds` ไม่รวมเวลารอคิวอีกต่อไป (แก้ comment แล้ว)
- ตัวสร้าง `ChatController` / `ChatStreamController` รับ `ChatAdmissionQueue` เพิ่ม (test เดิมปรับให้ใช้ `Admissions.Open()`)

## 4. ยังไม่ได้ทำ / ไม่ได้ยืนยัน (ตรงไปตรงมา)

- รหัส error/event ใหม่และตัวเลขความจุ **ยังไม่ได้รับอนุมัติ** (Issue ข้อ 6)
- ไม่ได้วัดโหลดหลายผู้ใช้จริงบนเครื่องเป้าหมาย (AI-012 อยู่นอกงานนี้); live check ที่ทำคือ 12 คำขอพร้อมกัน × 2 รอบกับ Ollama จริงบนเครื่องพัฒนา
- ไม่ได้ทดสอบ `MaxQueueWaitSeconds` กับ Ollama จริง (คำตอบสั้นเกินกว่าจะรอครบ 30 s) — พิสูจน์ด้วย mock เท่านั้น
- ไม่ได้ทดสอบหลายอินสแตนซ์ (ไม่รองรับ) และไม่ได้ทดสอบผ่าน reverse proxy
- ยังไม่ commit / push / เปิด PR (ต้องรอ owner สั่ง)

## 5. Definition of Done (Issue ข้อ 9)

- [x] Acceptance Criteria มีหลักฐานทำซ้ำได้ (ดู evidence)
- [ ] contract ได้รับการยอมรับจากเจ้าของ Backend/UI และ Leader
- [ ] ผ่าน review ตามแนวทางโครงการ
- [ ] Pull Request / merge (owner เท่านั้น)
