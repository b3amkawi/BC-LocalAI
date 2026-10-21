# BE-007 — Chat History API Contract (for FE-003)

วันที่ 2026-10-21 · Issue [#23](https://github.com/b3amkawi/BC-LocalAI/issues/23) · **Implemented** on `watzapon_BE-007`
Code: `ConversationsController`, `ChatController`, `ChatStreamController`, `ChatTurnService` · Tests: [Verification](BE-007-VERIFICATION.md)

## 1. Rules that hold for every call

- Available only when `ChatHistory:Enabled` is true (needs `Authentication:Enabled`). Off: every `/api/conversations` route is 404
  (before authentication), and a chat request with `conversationId` gets 404 `CONVERSATION_NOT_FOUND`.
- Authenticated User or Admin only (policy `ChatUser`, BE-006); anonymous → 401 `AUTH_REQUIRED`. The owner is the session's user id;
  no request field (body, query, header) can choose or name an owner. Admin has **no** access to other users' history.
- Someone else's conversation, a deleted one and an id that never existed give the **same** 404 `CONVERSATION_NOT_FOUND`.
- Unsafe methods (POST/PATCH/DELETE) are covered by BE-006's same-site guard (cross-site → 403 `AUTH_CSRF_INVALID`).
- Every response is `Cache-Control: no-store`. Errors: `{"error":{"code":"...","message":"..."}}`, no SQL/connection/stack details.
- Dates are UTC ISO 8601 with microseconds. Ids are UUIDs (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`).

## 2. Conversation management

| Method | Route | Request | Success |
|---|---|---|---|
| POST | `/api/conversations` | `{"title":"..."}` (optional; empty → `New chat`) | 201 + `Location`, conversation |
| GET | `/api/conversations?limit=20&cursor=...` | | 200 `{"items":[conversation...],"nextCursor":"..."\|null}` newest activity first |
| GET | `/api/conversations/{id}` | | 200 conversation |
| PATCH | `/api/conversations/{id}` | `{"title":"..."}` (required) | 200 conversation |
| DELETE | `/api/conversations/{id}` | | 204 (soft delete; it and its messages disappear from every call) |
| GET | `/api/conversations/{id}/messages?limit=20&cursor=...` | | 200 `{"items":[message...],"nextCursor":...}` oldest first |

Conversation: `{"id","title","createdAt","updatedAt"}`. Message:
`{"id","conversationId","sequence","role":"user|assistant","content","status","errorCode","createdAt","updatedAt"}`.

`status`: `completed` is the only finished answer. `pending` / `streaming` = still running (or abandoned by a crash: older than the
AI deadline + 60 s); `failed` (with `errorCode`) / `cancelled` = interrupted, `content` is the partial text the user saw.
**Show any non-`completed` answer as incomplete, never as an answer.** Render `content` as text (escape HTML).

Paging: `limit` 1–100 (default 20); `cursor` is opaque, pass back `nextCursor` unchanged. Paging is live (not a snapshot): a
conversation that gets a new message moves to the top, so the UI should de-duplicate by `id` when loading more.

## 3. Chat inside a conversation

`POST /api/chat` and `POST /api/chat/stream` take an optional `"conversationId"` next to `"message"`.

| | Without `conversationId` | With `conversationId` |
|---|---|---|
| Saved | nothing (unchanged behaviour) | question (completed) + answer (pending → streaming → final) |
| Sent to the AI | the question | earlier **completed** question/answer pairs (newest first within `ContextTokenBudget`, sent oldest first) + the question |
| `/api/chat` 200 | `{"response","model"}` | also `"conversationId"`, `"messageId"` (the saved answer), **only after the answer is stored** |
| `/api/chat/stream` | unchanged events | `started` also has `conversationId`, `messageId`; `completed` is sent **only after the full answer is stored** |

Errors before anything is generated (plain JSON, no SSE): 400 `INVALID_ID` / `INVALID_MESSAGE`, 404 `CONVERSATION_NOT_FOUND`,
409 `HISTORY_CONFLICT` (an earlier answer in this conversation is still running: one answer at a time per conversation),
503 `HISTORY_UNAVAILABLE` (history database down; the AI is not called).

After generation: if the answer cannot be stored, `/api/chat` answers 503 `HISTORY_UNAVAILABLE` (the answer is not reported as saved)
and the stream ends with `event: error` `HISTORY_UNAVAILABLE` instead of `completed`. AI errors, Stop / disconnect and an ended session
(`AUTH_SESSION_ENDED`) are stored as `failed` / `cancelled` with the partial text, and reported exactly as before BE-007.
If the owner deletes the conversation while the answer runs, nothing is stored and the reply is 404 / `event: error` `CONVERSATION_NOT_FOUND`.

## 4. Error codes (history)

| HTTP | Code | When |
|---|---|---|
| 400 | `INVALID_ID`, `INVALID_PAGE`, `INVALID_TITLE`, `INVALID_MESSAGE`, `INVALID_REQUEST` (malformed JSON) | bad input |
| 401 / 403 | BE-006 codes (`AUTH_REQUIRED`, `AUTH_CSRF_INVALID`, `AUTH_SESSION_ENDED`) | authentication |
| 404 | `CONVERSATION_NOT_FOUND` | missing, deleted or not yours |
| 409 | `HISTORY_CONFLICT` | an answer in this conversation is still running |
| 503 | `HISTORY_UNAVAILABLE` | history database unavailable |

The chat contract's own codes (`AI_*`, `INVALID_MESSAGE` for `/api/chat`) are unchanged.

## 5. Not provided (out of scope or later)

Sharing, admin read-all, restore of deleted conversations, physical purge, idempotency keys / retries, search, title generation.
