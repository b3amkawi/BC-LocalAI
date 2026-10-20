# BE-007 — History API Contract Proposal

วันที่ 2026-10-20 · **Draft for review, not implemented/not agreed for FE-003 integration yet**
BE-005/006 ยังไม่พร้อม: ไม่มี History routes, DB adapter, fake-auth fallback หรือ DI wiring ในแอป

## 1. Boundaries

- ทุก route ต้อง authenticated; UserId มาจาก stable server principal เท่านั้น ห้ามรับ userId/role จาก request body/query/header
- owner mismatch และ missing/deleted conversation ตอบ404เหมือนกัน; no admin-read-all shortcut
- mutation ต้องผ่าน approved Cookie/CSRF/session policy; anonymous401 / insufficient policy403 ก่อนทำ DB/AI work
- safe errors ใช้ `{"error":{"code":"...","message":"..."}}`; no-store, no raw SQL/connection/error text
- API ส่ง DTO เท่านั้น ไม่ expose persistence owner fields/stamps/internal sequence counters

## 2. Proposed endpoints

| Method | Route | Request / response |
|---|---|---|
| POST | `/api/conversations` | `{ "title": "ชื่อบทสนทนา" }` →201 safe conversation DTO + Location |
| GET | `/api/conversations?limit=20&cursor=...` | 200 `{ "items": [...], "nextCursor": null }` owner-only |
| GET | `/api/conversations/{id}` | 200 safe conversation DTO หรือ404 |
| PATCH | `/api/conversations/{id}` | title body →200 updated DTO หรือ404 |
| DELETE | `/api/conversations/{id}` | soft-delete →204; absent/not-owned/already-deleted404 ตาม repository draft |
| GET | `/api/conversations/{id}/messages?limit=20&cursor=...` | 200 chronological message page หรือ404 |

Conversation DTO: `id`, `title`, `createdAt`, `updatedAt` (UTC ISO8601)
Message DTO: `id`, `conversationId`, `sequence`, `role`, `content`, `status`, `errorCode`, `createdAt`, `updatedAt`
Status: pending/streaming/completed/failed/cancelled; partial content คือ content ของ non-completed message ไม่ใช่คำตอบสมบูรณ์
UI ต้องแสดงเป็น text/escape HTML โดยไม่เปลี่ยนข้อความที่เก็บไว้

## 3. Validation / pagination

- title: trim, default `New chat`, ไม่เกิน200 UTF-16 code units ตาม prototype, ไม่มี control chars/ill-formed Unicode
- UUID ไม่ว่าง/format ถูกต้อง; malformed id/body400; ไม่มี request field ที่กำหนด owner ได้
- limit1–100, default20; cursor opaque ≤256 ตัวอักษร ไม่มี whitespace, malformed400 `INVALID_PAGE`
- conversation order `UpdatedAt DESC, Id DESC`; cursor timestamps มาจาก UTC microseconds ที่อ่านกลับจาก DB
- messages order Sequence ASC; owner + conversation filter ใช้เสมอ ไม่เอา cursor เป็น authorization
- paging เป็น live keyset ไม่ใช่ snapshot: ระหว่างเปลี่ยน UpdatedAt อาจ reorder ต้องยืนยัน UX/dedup policy ก่อนจริง
- message storage ceiling Draft1MiB UTF-8 ไม่ใช่ input token budget; approved runtime limit อาจต่ำกว่านี้

## 4. Error contract proposal

| HTTP | Code | Behaviour |
|---|---|---|
| 400 | INVALID_TITLE / INVALID_PAGE / INVALID_MESSAGE / INVALID_MESSAGE_STATE | safe validation, no echo sensitive input |
| 401/403 | approved auth codes from BE-005/006 | ยังไม่กำหนดเองแทน auth contract |
| 404 | CONVERSATION_NOT_FOUND | ไม่มีการเปิดเผยว่า ID เป็นของคนอื่น |
| 409 | HISTORY_CONFLICT | concurrent state/write conflict; ห้าม overwrite terminal message |
| 503 | HISTORY_UNAVAILABLE | DB failure; ห้าม silently fall back to in-memory store |
| 500 | INTERNAL_ERROR | safe message/correlation metadata only |

Existing global ModelState mapping เป็น INVALID_MESSAGE: implementation ต้อง scope mapping สำหรับ History โดยไม่เปลี่ยน Chat เดิม

## 5. Streaming / AI handoff — unresolved by design

ยังไม่กำหนด route/body/event ใหม่สำหรับส่งข้อความใน conversation เพราะต้องตกลงกับ BE-004/AI/FE-003
ต้องระบุ stable message/request ID, duplicate/retry semantics, at most approved in-flight turns, transactional user+assistant placeholder,
durable checkpoints, partial/cancelled/error handling, disconnect cancellation และ process-crash reconciliation
AI completed event อย่างเดียวไม่พอที่จะอ้างว่าบันทึก durable แล้ว; persistence failure ต้องไม่รายงาน history saved
ห้ามส่งประวัติไป Ollama จนมี multi-turn/template/token budget contract ที่ยืนยันแล้ว
ไม่เปลี่ยน `/api/chat` หรือ `/api/chat/stream` เดิมและไม่เพิ่ม client-supplied UserId workaround
