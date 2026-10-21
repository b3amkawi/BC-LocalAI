# BE-007 - Persistent Chat History: design

Issue: #23 · Status: **implemented on `watzapon_BE-007`, off by default (`ChatHistory:Enabled`)** · Updated 2026-10-21
Results: [Verification](BE-007-VERIFICATION.md) · API for FE-003: [API contract](BE-007-API-CONTRACT.md) · [Migration plan](BE-007-MIGRATION-PLAN.md)

The 2026-10-20 groundwork (models, rules, context builder, repository contract, draft SQL; by the earlier BE-007 worktree) is kept.
On 2026-10-21 `main` with SEC-001 / BE-005 / BE-006 was merged in and the feature was completed on top of it.

## 1. Components

| Item | File | Notes |
|------|------|-------|
| Domain types, DTOs | `Models/ChatHistory/ChatHistoryModels.cs`, `ChatHistoryDtos.cs` | DTOs never carry the owner id or counters |
| Repository contract | `Services/ChatHistory/IChatHistoryRepository.cs` | owner on every call; not found = not yours |
| MySQL repository | `Services/ChatHistory/MySqlChatHistoryRepository.cs` | owner + DeletedAt in every statement, row lock for sequences |
| Rules / cursor | `ChatHistoryRules.cs`, `ChatHistoryCursor.cs` | titles, paging, message state machine |
| Context (D4) | `ConversationContextBuilder.cs`, `ConversationPrompt` in `ChatTurnService.cs` | token budget, complete pairs only, fixed template |
| Turn persistence (D3/D5) | `ChatTurnService.cs` | question + answer placeholder, checkpoints, one final state, 409 on double submit |
| API (D2) | `Controllers/ConversationsController.cs`, chat controllers | see the API contract |
| Settings / wiring | `Configuration/ChatHistoryOptions.cs`, `ChatHistoryRegistration.cs`, `Program.cs` | validated at startup; 404 gate when off |
| Migration (D1) | `database/chat-history/*.sql`, `scripts/chat-history/migrate.ps1` | operator tool, approved-target check |
| Tests | `BC-LocalAI.Tests/ChatHistory/*`, `tests/BE007.MySqlTests` | the same contract runs in memory and on MySQL |

## 2. Data model

### Conversations

| Column | Type (MySQL 8) | Rule |
|--------|----------------------|------|
| `Id` | `BINARY(16)` (UUID) | primary key; random, not guessable |
| `UserId` | `VARCHAR(64)` | stable user id from the session (BE-005/006); **never from the request body** |
| `Title` | `VARCHAR(200)` | trimmed, no control characters, empty -> "New chat" |
| `CreatedAt` | `DATETIME(6)` UTC | |
| `UpdatedAt` | `DATETIME(6)` UTC | moved forward by every new message or rename; sorts the list |
| `DeletedAt` | `DATETIME(6)` NULL | soft delete; a deleted conversation behaves as not found |
| `NextSequence` | `BIGINT` positive, default 1 | allocate/update under the same owner-filtered row lock and transaction |

### Messages

| Column | Type (MySQL 8) | Rule |
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

### DDL (migration 001, rehearsed on the isolated fixture)

Source of truth: [001-initial.up.sql](../../database/chat-history/001-initial.up.sql)
และ [001-initial.down.sql](../../database/chat-history/001-initial.down.sql)
ต้อง review รุ่น MySQL/target schema ก่อนใช้ ไม่มี runner หรือ schema creation ตอนเปิดแอป

UserId ใช้ `utf8mb4_0900_bin` เพื่อให้ case/accent/trailing spaces ไม่ถูกเทียบเป็นเจ้าของคนเดียวกัน
DDL เดิม `ai_ci` ขัดกับ exact identity contract จึงเลิกใช้
อ้างอิง [MySQL binary/NO PAD collations](https://dev.mysql.com/doc/mysql/8.0/en/charset-binary-collations.html)
UUID ใช้ canonical network byte order (ไม่ swap); .NET `Guid.ToByteArray(bigEndian: true)`/`new Guid(bytes, bigEndian: true)`
ไม่ใช้ byte layout จาก `Guid.ToByteArray()` ค่าเริ่มต้นโดยไม่ตกลงกับ SQL cursor order
DateTimeOffset ต้องแปลง UTC และอ่านค่าที่ persist แล้วใน precision microseconds ก่อนสร้าง cursor
UserId 64 ตัวอักษรพอสำหรับ `users.id` (UUID 36 ตัว) ของ BE-005; ถ้า id ยาวกว่านี้ต้องแก้ schema ก่อน

- The ownership check is in the query itself (`WHERE c.Id = @id AND c.UserId = @userId AND c.DeletedAt IS NULL`), always parameterized.
- Proposed sequence allocation: `SELECT NextSequence ... FOR UPDATE` with owner + not-deleted predicate, increment, insert message,
  update conversation activity, commit atomically. Unique key is a backstop, not the locking mechanism.
  ไม่ใช้ bare `MAX(Sequence)+1` โดยไม่มี lock และยังไม่ได้ทดสอบ concurrency กับ MySQL จริง
- Store messages at most 1 MiB UTF-8 (storage ceiling, not an AI token budget); reject ill-formed UTF-16 rather than silently replacing it.
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

ต่อเข้ากับ chat แล้ว (2026-10-21): `ChatTurnService` อ่านข้อความล่าสุด (ไม่เกิน 200) → builder (budget `ChatHistory:ContextTokenBudget` 1500)
→ `ConversationPrompt` ใส่เฉพาะคู่ คำถาม/คำตอบ ที่ completed ทั้งคู่ ลงใน template คงที่ (`<conversation>` ... `</conversation>`,
ข้อความที่เก็บไว้ปิด/เปิด block เองไม่ได้) แล้วตามด้วยคำถามปัจจุบัน; ไม่มีคู่ก่อนหน้า = prompt คือคำถามเดิมทุกตัวอักษร
AI-002 request ไม่เปลี่ยน (ยัง `/api/generate`, system prompt และ JSON answer format เดิม) — template และ budget รอ AI Engineer review
ด้วย tokenizer จริง (system + template + history + คำถาม + output 1024 + margin ต้องไม่เกิน context window ของ qwen3:4b ที่ใช้)

## 5. Migration and rollback plan

ดู [BE-007-MIGRATION-PLAN.md](BE-007-MIGRATION-PLAN.md): แยก schema/migrator/runtime permissions,
DDL implicit commits, partial-apply recovery, empty-only rollback guard และห้าม auto-delete เมื่อมีข้อมูล
Feature flag `ChatHistory:Enabled` (false): ปิดอยู่ = route 404 และไม่แตะฐาน; เปิดได้เมื่อ auth เปิด, migration apply แล้ว
และมี connection string ของ `bc_history_rw` (ตรวจตอนเริ่มแอป) — rollback ของแอป = ปิด flag, ข้อมูลยังอยู่

### Retention / deletion (soft delete now; period pending the data owner)

- Soft delete ซ่อน conversation และ messages จากทุก owner query ทันที; ไม่เท่ากับลบข้อมูลจริง
- ไม่มี admin-read-all, sharing, restore/purge API หรือ background purge job (runtime account ไม่มีสิทธิ์ DELETE)
- ไม่ตั้ง automatic purge และไม่อ้างว่า retention แบบไม่มีกำหนดได้รับอนุมัติ
- Leader/data owner ต้องยืนยันระยะเก็บ, grace period, physical erasure ของ messages/backups/audit และ legal constraints ก่อนใช้กับพนักงาน
- Physical deletion ต้องอยู่ใน transaction/approved job พร้อมสิทธิ์เฉพาะ ไม่แจก DELETE/DDL ให้ runtime โดยไม่จำเป็น

## 6. Decisions (2026-10-21, watzapon on Claude Code's recommendation, delegated by the project owner)

| # | Question | Decision |
|---|----------|----------|
| 1 | Where is history stored? | The BC-LocalAI MySQL database (`bc_localai_sec001`, same server as the accounts), own tables, own runtime account `bc_history_rw` with SELECT/INSERT/UPDATE on the two tables only (no DELETE, no DDL, no access to `users`), TLS required. Migrations by a separate migrator account. |
| 2 | Stable user id | `users.id` (UUID) from the BE-006 session claim `NameIdentifier`; never from the request. No foreign key to `users` (accounts are disabled, not deleted). |
| 3 | Retention / deletion | Soft delete (`DeletedAt`); no automatic purge and no restore API yet. Retention period and physical erasure (including backups) are for the data owner to decide before go-live. |
| 4 | Admin reads other users' history? | No (out of scope in the issue; matches SEC-001). |
| 5 | How is context sent to Ollama? | The AI-002 request is unchanged: one prompt to `/api/generate`, same system prompt and JSON answer format. Earlier completed pairs are written before the question in a fixed template (`ConversationPrompt`); with no earlier pair the prompt is exactly the question. **For review by the AI Engineer.** |
| 6 | Context budget / message size | `ChatHistory:ContextTokenBudget` 1500 estimated tokens for earlier turns (heuristic estimator; answers keep `MaxOutputTokens` 1024). Stored message ceiling 1 MiB. |
| 7 | Data access library | MySqlConnector (already used by BE-005), plain parameterized ADO.NET; no new package. |

## 7. Verification of the original groundwork (historical, 2026-10-20)

- 93 new unit tests (`FullyQualifiedName~ChatHistory`), all passing; the existing suite is unchanged.
- Mutation check: each deliberate bug makes tests fail: ownership not checked (2), final states changeable (14), incomplete answers used as
  context (4), skipping a big turn instead of stopping (1), cursor without a tie-breaker (1), `completed` allowed with empty content (2).
- At that time not verified: real database, `Sequence` concurrency, API — all verified on 2026-10-21 (see Verification).

ผลปัจจุบัน (database จริง, API, mutation) และ AC mapping: [Verification](BE-007-VERIFICATION.md) · API: [API Contract](BE-007-API-CONTRACT.md)
