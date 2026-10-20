# BE-007 - Persistent Chat History: design draft (groundwork)

Issue: #23 · Status: **draft for review, not wired into the application** · Branch: `watzapon05_Backend`

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

Not done on purpose (needs BE-005 / BE-006 / BE-004 or a decision): database access code, migrations, API endpoints, DI registration,
streaming persistence, retention job. `POST /api/chat` is unchanged.

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

```sql
-- Draft for review. Target database and account are open questions (section 6).
CREATE TABLE Conversations (
  Id         BINARY(16)   NOT NULL,
  UserId     VARCHAR(64)  NOT NULL,
  Title      VARCHAR(200) NOT NULL,
  CreatedAt  DATETIME(6)  NOT NULL,
  UpdatedAt  DATETIME(6)  NOT NULL,
  DeletedAt  DATETIME(6)  NULL,
  PRIMARY KEY (Id),
  KEY IX_Conversations_User_Updated (UserId, DeletedAt, UpdatedAt DESC, Id DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE Messages (
  Id             BINARY(16)   NOT NULL,
  ConversationId BINARY(16)   NOT NULL,
  Sequence       BIGINT       NOT NULL,
  Role           VARCHAR(16)  NOT NULL,
  Content        MEDIUMTEXT   NOT NULL,
  Status         VARCHAR(16)  NOT NULL,
  ErrorCode      VARCHAR(64)  NULL,
  CreatedAt      DATETIME(6)  NOT NULL,
  UpdatedAt      DATETIME(6)  NOT NULL,
  PRIMARY KEY (Id),
  UNIQUE KEY UX_Messages_Conversation_Sequence (ConversationId, Sequence),
  CONSTRAINT FK_Messages_Conversations FOREIGN KEY (ConversationId) REFERENCES Conversations (Id),
  CONSTRAINT CK_Messages_Role   CHECK (Role IN ('user', 'assistant')),
  CONSTRAINT CK_Messages_Status CHECK (Status IN ('pending', 'streaming', 'completed', 'failed', 'cancelled')),
  CONSTRAINT CK_Messages_ErrorCode CHECK ((Status = 'failed') = (ErrorCode IS NOT NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```

- The ownership check is in the query itself (`WHERE c.Id = @id AND c.UserId = @userId AND c.DeletedAt IS NULL`), always parameterized.
- `Sequence` is allocated inside a transaction (`SELECT ... FOR UPDATE` on the conversation row, or `MAX(Sequence)+1` with the unique key
  as the safety net and a retry), so two messages sent at the same time never get the same position.

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
4. returned oldest-first, with the estimated token count and how many messages were dropped.

The default `Utf8TokenEstimator` counts one token per 3 UTF-8 bytes (+4 per message). It over-estimates Thai (measured: 384 Thai characters
= 222 tokens with qwen3:4b; the estimate gives 388), so the context is cut too early rather than too late. The budget value itself is an
open question for AI-004 (context window is 4096 tokens by default; BE-003 already reserves up to 1024 for the answer).

## 5. Migration and rollback plan (draft)

1. Create the two tables in a **separate schema** with a dedicated account (`SELECT/INSERT/UPDATE` on these tables only, no DDL at run time).
2. Forward migration: only `CREATE TABLE`; no existing table is touched, so there is nothing to lose on rollback.
3. Rollback before go-live: `DROP TABLE Messages; DROP TABLE Conversations;` (order matters because of the foreign key).
   After go-live a rollback **deletes users' history**: back up (`mysqldump` of the schema) first and get approval (AGENTS.md section 9).
4. The application feature flag stays off until BE-005/006 are merged and the contract tests pass against the real database.

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

## 7. Verification of this groundwork

- 93 new unit tests (`FullyQualifiedName~ChatHistory`), all passing; the existing suite is unchanged.
- Mutation check: each deliberate bug makes tests fail: ownership not checked (2), final states changeable (14), incomplete answers used as
  context (4), skipping a big turn instead of stopping (1), cursor without a tie-breaker (1), `completed` allowed with empty content (2).
- Not verified: anything against a real database (none exists for this yet), concurrency of `Sequence` allocation in MySQL, API behaviour.
