# BE-007 — Verification Report (D7)

วันที่ 2026-10-21 (Asia/Bangkok) · Issue [#23](https://github.com/b3amkawi/BC-LocalAI/issues/23) · Branch `watzapon_BE-007`
(worktree `BC_BE-007`, local branch `be007-work`) · Base: groundwork `4011871` + `main` `babbd61` (SEC-001, BE-005, BE-006) merged in `63e269f`
Machine: test machine Acer Predator PHN16-72, RTX 4060 Laptop 8 GB (reference spec RTX 4050 Laptop 6 GB) · MySQL 8.0 isolated fixtures only

[Design](BE-007-DESIGN.md) · [API contract for FE-003](BE-007-API-CONTRACT.md) · [Migration plan](BE-007-MIGRATION-PLAN.md)

## 1. Scope and decisions

Built on the 2026-10-20 groundwork (kept): MySQL repository, migration tool, conversation API, chat/stream persistence with
multi-turn context, settings and startup validation, tests and documents. Decisions (design section 6) were taken by watzapon on
Claude Code's recommendation: same MySQL database with an own least-privilege runtime account, `users.id` as owner, soft delete with
retention pending the data owner, no admin read-all, context written into the unchanged AI-002 prompt (template for AI Engineer
review), budget 1500 estimated tokens, MySqlConnector (no new package). **Off by default** (`ChatHistory:Enabled=false`).

Files outside chat history: `Program.cs` (registration, 404 gate, history-only model-state code), `ChatController` /
`ChatStreamController` (optional `conversationId`), `ChatRequest` / `ChatResponse` / `StreamStartedEvent` (optional fields, absent
without a conversation), `tools/Sec001Fixtures` + `scripts/security/local-mysql.ps1` (opt-in `be007-mysql` fixture),
`TestSupport.cs` (pins `ChatHistory:*`), `docs/devops/test-baseline.json` (.NET 781 → 835), README.

## 2. Tests actually run

| Suite | Result |
|---|---|
| `dotnet build BC-LocalAI.slnx -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test BC-LocalAI.slnx` (Development) | **835** passed (after merging main with the AI review fixes) |
| Same, `ASPNETCORE_ENVIRONMENT=Production` | **835** passed |
| Same with hostile environment (`Authentication__Enabled=true`, idle 999, absolute 1, `MaxSessions=1`, `ChatHistory__Enabled=true`, budget 1, checkpoint 60, `MaxQuestionTokens=5`) | **835** passed (settings pinned in tests) |
| `node --test "tests/ui/*.test.mjs"` | 61 passed |
| `python -m unittest discover -s tests` | 143 passed |
| `check_test_counts.py` / secret scan / doc links / CI script tests | all gates passed / clean / OK / OK |
| Live MySQL `tests/BE007.MySqlTests` (fixture `be007-mysql`, 127.0.0.1:3320) | **53** passed, 1 skipped by default (real-model check below) |
| Real model: same suite with `BE007_LIVE_OLLAMA=1`, Ollama qwen3:4b | **1** passed, 3 runs |
| Live MySQL `tests/BE005.MySqlTests` on a **fresh** `be005-mysql` fixture built with the changed fixture tool | 16 passed (BE-005 + BE-006 unaffected) |
| Fixture checks (`local-mysql.ps1 -Action Verify`) | 11/11 on both fixtures |

What the tests cover:

- **Repository contract** (runs unchanged on the in-memory reference and on MySQL): owner on every call, same answer for missing /
  deleted / someone else's (IDOR), exact owner comparison (case, trailing space), paging without gaps or duplicates (timestamp ties),
  forged cursors, message state machine, Thai/emoji text unchanged, recent messages for context, question + answer placeholder
  created together with consecutive sequence numbers, cancellation changes nothing.
- **MySQL only**: 24 parallel writers get sequence numbers 1–24 (row lock), data survives a new connection pool, ids stored in RFC byte
  order and times in UTC microseconds (what the API returns equals what is stored), CHECK constraints refuse a blank completed answer /
  a failed answer without a code / an unknown status even when the C# rules are bypassed, the runtime account cannot DELETE, read
  `users`, DROP or ALTER, a stale writer cannot reopen a completed answer, database down → `ChatHistoryUnavailableException` without
  inner exception, port or password.
- **API (real pipeline, BE-006 sessions)**: anonymous 401; create/list/get/rename/delete contract, `Location`, `no-store`, DTO fields
  only (no owner id); another user gets byte-for-byte the same 404 as for an unknown id on all 6 routes (incl. chat and stream) and
  changes nothing; 400 codes for bad id / limit / cursor / JSON / title; paging through the API.
- **Chat in a conversation**: turn saved, earlier completed pairs sent as context (first question sent exactly as typed); a failed
  answer stored as `failed` with its code, never `completed`, and not reused as context; context budget keeps the newest turns;
  double submit → 409 and the AI is not called, an answer abandoned longer than the AI deadline + 60 s no longer blocks, reads as
  `failed INTERRUPTED` (reading never writes) and is closed as such at the next question; storage down
  → 503 before the AI is called (chat, stream, list); conversation deleted while answering → 404, nothing stored; session ended while
  waiting → 401 and the answer stored as `failed AUTH_SESSION_ENDED`, its text not returned or stored.
- **Stream**: `started` carries the ids; the answer stays `pending` while pieces arrive and becomes `completed` only after the full
  text is stored (then `completed` is sent); partial text saved at checkpoints; an AI error mid-stream stored as `failed` with the
  partial text; session ended mid-stream stored as `failed AUTH_SESSION_ENDED`.
- **Off by default**: every history route 404 in Development and Production (also POST/PATCH/DELETE), chat with a conversation 404,
  chat without one unchanged (no new fields), and a repository that fails the test if touched is never touched.
- **Startup**: history on with authentication off → the app refuses to start.
- **Real stack** (`Be007LiveApiTests`): BE-005 login against MySQL → conversation → two chats with context → a new application host
  (restart) still lists the same 4 completed messages; a conversation of another user inserted in the database → 404 on every route,
  unchanged, no message written.
- **Retention purge** (`Be007PurgeTests`, real fixture): Preview counts only and changes nothing; Purge erases exactly the
  conversation deleted 31 days ago (not 29), all history of an account closed 91 days ago (not 89), a conversation whose owner no
  longer exists after 91 days (not 10), and keeps an active (only locked) account's history even when its row is 200 days old and an
  active user's 1000-day-old conversation; no orphan messages; audit line has the counts and no ids, titles or text; a second run
  finds nothing; refusals (no audit file, missing audit folder, wrong target) change nothing; the purge account cannot read
  `password_hash` / `security_stamp` (1143), cannot UPDATE / INSERT history or DELETE users or DROP (1142).
- **Real model** (`Be007LiveOllamaTests`, qwen3:4b, temperature 0): told "ฉันชื่อสมชาย … ชอบสีเขียว", the next `/api/chat` answer contains
  "สมชาย" and a following `/api/chat/stream` answer contains "เขียว"; all 6 messages stored `completed`, so the JSON answer format of
  AI-002 still holds with the context template. This shows the template works on this model and machine; it is not a quality
  evaluation of multi-turn answers.

### Mutation check (each deliberate bug made tests fail)

Application: failed pairs used as context (1) · no double-submit guard (1) · token budget ignored (1) · stored text can close the
history block (3) · stream sends `completed` without saving (1) · no stream checkpoints (1) · stream AI error not recorded (1) · chat AI
error saved as completed (1) · history routes reachable while disabled (7) · chat session end not recorded (**survived first**; test
added, then 1).
MySQL (live): owner filter removed (4) · deleted conversations visible (2) · no row lock for sequence numbers (1) · update of any
owner's message (1) · times not truncated to microseconds (1) · turn insert advances the sequence counter by 1 instead of 2 (2).
Follow-up round: abandoned answer not shown as interrupted (1) · abandoned answer not closed at the next question (1).
Purge script: deleted conversations measured with 90 days (1) · closed accounts measured from the conversation instead of the account
(2) · conversations of removed owners ignored (1) · active accounts purged when their row is old (**survived first**; test extended with
an active 200-day-old account, then 1).

### Migration / rollback rehearsal

See [Migration plan section 4](BE-007-MIGRATION-PLAN.md): Up refused on existing tables, Down refused without confirmation and on
non-empty tables (guard routine cleaned up), Down on empty tables, Up again with runtime grants still working, wrong target refused.

## 3. Acceptance criteria (Issue #23)

| Criterion | Status |
|---|---|
| Database schema for Conversations and Messages | ✅ migration 001, applied on the fixture |
| Migration and rollback plan | ✅ tool + plan + rehearsal (production backup/restore rehearsal: operator, before go-live) |
| Conversations bound to a stable user id | ✅ `users.id` from the session; never from the client |
| Several conversations per user | ✅ |
| Conversation management APIs | ✅ create, list, get, rename, delete, messages |
| Message persistence | ✅ question completed; answer pending → streaming → final |
| Multi-turn context | ✅ complete pairs, newest first, unchanged AI-002 request; real-model check passed |
| Context token budget | ✅ `ContextTokenBudget` (estimate; AI Engineer to confirm with the tokenizer) |
| Streaming message persistence | ✅ checkpoints + one final state |
| Incomplete response never stored as completed | ✅ code, CHECK constraints, tests, mutations |
| Ownership authorization / IDOR prevention | ✅ in every SQL statement; same 404; live + in-process tests |
| Pagination | ✅ keyset cursors |
| Data retention / deletion policy | ✅ approved 2026-10-21 (30 days after delete, 90 days after account closed, backups ≤ 30 days); operator purge script, own account, audit, tested live |
| Safe error handling | ✅ 400/404/409/503 codes, no database details |
| Unit / integration tests, security verification | ✅ sections 2 |
| No cross-user data leakage | ✅ tested in-process and on MySQL |
| Existing chat API not regressed | ✅ all existing chat tests pass; without `conversationId` no new fields and nothing stored |
| Code reviewed per AGENTS.md | ✅ Claude Code review (this report, mutation checks) accepted by watzapon on 2026-10-21, with CI on the pull request |

## 4. Limitations / remaining

- **Retention**: the purge is an operator job (schedule it daily); backup rotation (≤ 30 days) must be configured where backups are made;
  no restore API (an administrator can recover a deleted conversation in the database within 30 days).
- **AI Engineer review**: the prompt template and the 1500-token estimate (system + template + history + question + 1024 output must fit
  the model's context window); the real-model check is one scenario on one machine.
- **Crash recovery**: an answer interrupted by a process crash is shown as `failed INTERRUPTED` after the AI deadline + 60 s and is
  closed in the database at the next question in that conversation (no background job; a conversation never used again keeps the raw
  `pending`/`streaming` row, which the API still shows as interrupted).
- **Concurrency**: one answer per conversation (409); no idempotency key, so a retried request after a lost response creates a new turn.
- Paging is live, not a snapshot (documented for FE-003).
- Not enabled in a real deployment until FE-003, HTTPS, the history account and the migration are in place.

## 5. After PR #36 (2026-10-21)

PR #36 was merged by Pao after his AI review (`a6bcd0b`: token estimate calibrated on qwen3:4b, `MaxQuestionTokens`, turn-line
neutralizing; see [AI review](BE-007-AI-REVIEW.md)). The retention purge (section 2) was finished after that and is merged with `main`
`3bbdcc4` in this branch. Found while re-testing: `ChatHistory:MaxQuestionTokens` was not pinned in the shared test settings, so an
environment variable could change unrelated tests (now pinned); `OllamaTimeoutCancellationTests.Deadline_DoesNotCutInWhenTheAnswerArrivesInTime`
(BE-003) failed once under load because its stub answered after 300 ms of a 1 s deadline (now 50 ms; 3 full runs passed).
Live suite after the merge: 53 passed; real qwen3:4b check passed.
