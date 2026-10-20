# BE-007 - Persistent Chat History: design draft (groundwork)

Issue: #23 · Status: **draft for review, not wired into the application** · Branch: `watzapon_BE-007`

อัปเดต 2026-10-20: pull `main` ที่รวม BE-004/FE-002 แล้ว (`91916ea`) เข้า branch ที่ `258f23c`
รอบนี้ทำเฉพาะส่วนเตรียมตาม Dependencies ของ Issue ไม่ทำ Login, History Controller, DB adapter หรือ DI wiring
ไม่มีการ merge งาน SEC-001 เข้ามา และไม่ใช้ฐานบัญชีของบริษัท/ฐานทดสอบ SEC-001 เป็นที่เก็บ History
ผลทดสอบล่าสุดและสิ่งที่ยังไม่ได้ทำอยู่ [BE-007-VERIFICATION.md](BE-007-VERIFICATION.md)

BE-007 needs BE-005 (login) and BE-006 (session / authorization) before it can be switched on. The issue allows this part to start
early: *"you can start designing the database schema and repository interfaces before BE-006 is done, but a stable user identity and a
confirmed authorization contract are needed before real use"*. This document and the code listed below are that early part only.

## 1. What exists now

| Item | File | Notes |
|------|------|-------|
| Domain types | `BC-LocalAI/Models/ChatHistory/ChatHistoryModels.cs` | `Conversation`, `ChatMessage`, `MessageRole`, `MessageStatus`, `PageRequest`, `Page<T>` |
| Repository contract | `BC-LocalAI/Services/ChatHistory/IChatHistoryRepository.cs` | every call takes the user id from the session; not-found and not-yours look the same |
| Business rules | `BC-LocalAI/Services/ChatHistory/ChatHistoryRules.cs` | title rules, page limits, message state machine |
| Multi-turn context builder (D4) | `BC-LocalAI/Services/ChatHistory/ConversationContextBuilder.cs` | token budget, newest turns first, completed messages only |
| Reference implementation | `BC-LocalAI.Tests/ChatHistory/InMemoryChatHistoryRepository.cs` | test-only; defines the expected behaviour |
| Contract tests | `BC-LocalAI.Tests/ChatHistory/ChatHistoryRepositoryContractTests.cs` | abstract: the future database repository inherits it and must pass the same tests |

เพิ่มไฟล์ Migration/Rollback **Draft** ใน `database/chat-history/` พร้อม static contract checks แล้ว แต่ยังไม่ execute กับ MySQL
Not done on purpose (needs BE-005 / BE-006 or a decision): database access code, applied migrations, API endpoints, DI registration,
streaming persistence, retention job. `POST /api/chat` และ SSE contract เดิมไม่เปลี่ยน

## 2. Data model

### Conversations

| Column | Type (MySQL 8 draft) | Rule |
|--------|----------------------|------|
| `Id` | `BINARY(16)` (UUID) | primary key; random, not guessable |
| `UserId` | `VARCHAR(64)` | stable user id from the session (BE-005/006); **never from the request body** |
| `Title` | `VARCHAR(200)` | trimmed, no control characters, empty -> "New chat" |
| `CreatedAt` | `DATETIME(6)` UTC | |
| `UpdatedAt` | `DATETIME(6)` UTC | moved forward by every new message or rename; sorts the list |
| `DeletedAt` | `DATETIME(6)` NULL | soft delete; a deleted conversation behaves as not found |
| `NextSequence` | `BIGINT` positive, default 1 | allocate/update under the same owner-filtered row lock and transaction |

### Messages

| Column | Type (MySQL 8 draft) | Rule |
|--------|----------------------|------|
| `Id` | `BINARY(16)` | primary key |
| `ConversationId` | `BINARY(16)` | foreign key -> `Conversations.Id` |
| `Sequence` | `BIGINT` | order inside the conversation, unique per conversation |
| `Role` | `VARCHAR(16)` | `user` / `assistant` |
| `Content` | `MEDIUMTEXT` utf8mb4 | Thai and emoji need `utf8mb4` |
| `Status` | `VARCHAR(16)` | `pending` / `streaming` / `completed` / `failed` / `cancelled` |
| `ErrorCode` | `VARCHAR(64)` NULL | API error code when `failed` (for example `AI_TIMEOUT`) |
| `CreatedAt`, `UpdatedAt` | `DATETIME(6)` UTC | |

### Message states

```
assistant:  pending ──> streaming ──> streaming ...
               │            │
               └────────────┴──> completed | failed (needs ErrorCode) | cancelled      (final, never change again)
user:       completed on insert, never changes
```

- **Completed needs content**, and only `completed` messages are ever shown as answers or used as context
  (issue: "do not save an incomplete response as Completed").
- An interrupted stream keeps its partial text with `failed` / `cancelled`, so the UI can show "the answer was interrupted".

### DDL draft (not executed anywhere)

Source of truth: [001-initial.up.sql](../../database/chat-history/001-initial.up.sql)
และ [001-initial.down.sql](../../database/chat-history/001-initial.down.sql)
ต้อง review รุ่น MySQL/target schema ก่อนใช้ ไม่มี runner หรือ schema creation ตอนเปิดแอป

UserId ใช้ `utf8mb4_0900_bin` เพื่อให้ case/accent/trailing spaces ไม่ถูกเทียบเป็นเจ้าของคนเดียวกัน
DDL เดิม `ai_ci` ขัดกับ exact identity contract จึงเลิกใช้
อ้างอิง [MySQL binary/NO PAD collations](https://dev.mysql.com/doc/mysql/8.0/en/charset-binary-collations.html)
UUID ใช้ canonical network byte order (ไม่ swap); .NET `Guid.ToByteArray(bigEndian: true)`/`new Guid(bytes, bigEndian: true)`
ไม่ใช้ byte layout จาก `Guid.ToByteArray()` ค่าเริ่มต้นโดยไม่ตกลงกับ SQL cursor order
DateTimeOffset ต้องแปลง UTC และอ่านค่าที่ persist แล้วใน precision microseconds ก่อนสร้าง cursor
UserId 64 ตัวอักษรเป็น Draft เท่านั้น ถ้า BE-005 ยืนยัน ID ยาวกว่านี้ต้องแก้ schema/contract ก่อนใช้

- The ownership check is in the query itself (`WHERE c.Id = @id AND c.UserId = @userId AND c.DeletedAt IS NULL`), always parameterized.
- Proposed sequence allocation: `SELECT NextSequence ... FOR UPDATE` with owner + not-deleted predicate, increment, insert message,
  update conversation activity, commit atomically. Unique key is a backstop, not the locking mechanism.
  ไม่ใช้ bare `MAX(Sequence)+1` โดยไม่มี lock และยังไม่ได้ทดสอบ concurrency กับ MySQL จริง
- Store messages at most 1 MiB UTF-8 (Draft ceiling, not an AI token budget); reject ill-formed UTF-16 rather than silently replacing it.
  SQL CHECK ป้องกันค่าผิดพื้นฐาน แต่การตรวจ whitespace Unicode, enum, error code, state transitions และการห้ามแก้ terminal state ต้องผ่าน service/repository ด้วย
- Indexes target owner/deleted/activity/id pagination and conversation/sequence message order; production query plans remain unmeasured.

## 3. Repository contract (summary of `IChatHistoryRepository`)

| Rule | How it is enforced / tested |
|------|-----------------------------|
| The user id comes from the authenticated session, not from the client | parameter on every method; the API layer (later) passes the claim |
| Not found and owned by someone else are the same answer (null / false -> 404) | `AnotherUser_CannotSeeChangeOrUseAConversation_AndCannotTellItExists` |
| Deleted = not found, including its messages | `Delete_HidesTheConversationAndItsMessages` |
| Lists are paged with an opaque cursor (limit 1-100), keyset order, no gaps or duplicates | `List_PagesThroughEverything_WithoutGapsOrDuplicates`, `Messages_PageInOrder`, forged cursor -> `INVALID_PAGE` |
| Message state machine | `ChatHistoryRulesTests` (all allowed and refused transitions), contract tests for streaming / interrupted / refused updates |
| Thai, emoji, combining marks, line breaks stored unchanged | `Messages_KeepTheirOrder_AndThaiTextIsUnchanged` |
| Validation errors carry stable codes | `INVALID_TITLE`, `INVALID_PAGE`, `INVALID_MESSAGE`, `INVALID_MESSAGE_STATE` |

## 4. Multi-turn context (D4)

`ConversationContextBuilder.Build(history, tokenBudget)`:

1. only `completed` messages;
2. grouped into turns (a question with its answer), kept or dropped together;
3. newest turns kept first; the first turn that does not fit stops the walk (no holes in the conversation);
4. returned oldest-first, with the estimated token count and how many completed messages were dropped;
5. reject mixed conversations, duplicate/nonpositive sequences, undefined enums and negative estimates; ignore orphan assistant messages;
6. accumulate costs using long and compare remaining budget to avoid int overflow.

The default `Utf8TokenEstimator` counts one token per 3 UTF-8 bytes (+4 per message). **This is a heuristic, not a guaranteed upper bound.**
ตัวอย่างภาษาไทยเดียวไม่ยืนยันทุก prompt/template; ห้ามอ้างว่าระบบจะไม่เกิน context window จาก estimator นี้
รอ contract จาก AI Engineer (ข้อสังเกตใน Issue เสนอ AI-008 แต่ยังไม่ถือว่ามีงานนั้นอนุมัติแล้ว)
ต้องรวม system/template/history/current question/output reserve/safety margin และทดสอบภาษาไทยจริงก่อนเชื่อม Ollama
Builder ใน branch นี้เป็น offline prototype เท่านั้น ไม่มีการเปลี่ยน AI-002 จาก generate prompt เป็น chat messages

## 5. Migration and rollback plan (draft)

ดู [BE-007-MIGRATION-PLAN.md](BE-007-MIGRATION-PLAN.md): แยก schema/migrator/runtime permissions,
DDL implicit commits, partial-apply recovery, empty-only rollback guard และห้าม auto-delete เมื่อมีข้อมูล
ไม่มี feature flag ที่ implement อยู่แล้ว: ขณะนี้ไม่มี DI registration/History route เลย จึงไม่เปิด persistence โดยปริยาย
ต้องผ่าน BE-005/006 และ real database/authorization integration tests ก่อน enable

### Retention / deletion proposal (not approved)

- Soft delete ซ่อน conversation และ messages จากทุก owner query ทันที; ไม่เท่ากับลบข้อมูลจริง
- ไม่มี admin-read-all, sharing, restore/purge API หรือ background purge job ในรอบเตรียมนี้
- ไม่ตั้ง automatic purge และไม่อ้างว่า retention แบบไม่มีกำหนดได้รับอนุมัติ
- Leader/data owner ต้องยืนยันระยะเก็บ, grace period, physical erasure ของ messages/backups/audit และ legal constraints ก่อนใช้กับพนักงาน
- Physical deletion ต้องอยู่ใน transaction/approved job พร้อมสิทธิ์เฉพาะ ไม่แจก DELETE/DDL ให้ runtime โดยไม่จำเป็น

## 6. Open questions (need a decision before implementation)

| # | Question | Why it matters |
|---|----------|----------------|
| 1 | Which database stores chat history? The company MySQL is **read-only** for BC-LocalAI (BE-005), so history needs its own schema or server. | write account, backup, who owns the data |
| 2 | What is the stable user id (employee id? database key)? Can it change? | `UserId` column; comes from SEC-001 / BE-005 |
| 3 | Retention: how long are conversations kept? Is delete soft (recoverable) or must it be erased? | `DeletedAt`, purge job, privacy policy |
| 4 | Does an admin ever read other users' history? (BE-007 lists "Admin Read-All" as out of scope) | keep the "owner only" rule strict |
| 5 | How is multi-turn context sent to Ollama? The AI-002 contract uses `/api/generate` with one prompt; `/api/chat` with a `messages` array would change the contract. | needs the AI Engineer |
| 6 | Context token budget and maximum stored message size | AI-004 benchmark |
| 7 | Data access library (ADO.NET with `MySqlConnector`, Dapper, EF Core)? | new dependency: licence and security review per AGENTS.md section 6 |

## 7. Verification of the original groundwork (historical)

- 93 new unit tests (`FullyQualifiedName~ChatHistory`), all passing; the existing suite is unchanged.
- Mutation check: each deliberate bug makes tests fail: ownership not checked (2), final states changeable (14), incomplete answers used as
  context (4), skipping a big turn instead of stopping (1), cursor without a tie-breaker (1), `completed` allowed with empty content (2).
- Not verified: anything against a real database (none exists for this yet), concurrency of `Sequence` allocation in MySQL, API behaviour.

ผลปัจจุบัน/AC mapping ดู [Verification](BE-007-VERIFICATION.md)
Proposed API สำหรับรีวิวก่อน FE-003 ใช้งานจริงอยู่ [API Contract](BE-007-API-CONTRACT.md)
