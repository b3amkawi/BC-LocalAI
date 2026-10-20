# FE-002 - Streaming Chat UI: behaviour and API integration (D5)

Issue: #18 · Branch: `watzapon_FE-002` · Contract used: [BE-004 SSE contract](BE-004-SSE-CONTRACT.md) (merged in PR #31)

## 1. Files

| File | Role |
|------|------|
| `BC-LocalAI/wwwroot/js/chat-stream.js` | **new**, no DOM: SSE parser, answer state machine, `readAnswer()` (reads a `fetch` Response with `ReadableStream` + streaming `TextDecoder`). Unit-tested in Node |
| `BC-LocalAI/wwwroot/js/chat.js` | page wiring: calls only `POST /api/chat/stream`, renders one bubble per answer, Stop button / Esc, states, scrolling |
| `BC-LocalAI/wwwroot/js/chat-core.js` | FE-001 core; new message `STREAM_INTERRUPTED` |
| `BC-LocalAI/Views/Home/Index.cshtml` | Stop button (`#stop`, hidden until needed), `data-state` on the form, loads `chat-stream.js` between core and page script |
| `BC-LocalAI/wwwroot/css/chat.css` | typing caret, status line, "incomplete" marking, note bubble, Stop button (reduced-motion respected) |

No new dependency, no framework, no `EventSource` (it cannot send a POST body). The page still talks only to its own origin (CSP `connect-src 'self'`).

## 2. Request and stream handling

1. `fetch('/api/chat/stream', { method: 'POST', JSON body { message }, signal })` with one `AbortController` per question.
2. HTTP error before the stream (e.g. `400 INVALID_MESSAGE`): the JSON error contract is read and shown as in FE-001.
3. A `200` that is not `text/event-stream`: `AI_INVALID_RESPONSE`.
4. Body read chunk by chunk; bytes go through `TextDecoder('utf-8')` with `stream: true`, so a Thai character or an emoji split across network reads is joined correctly.
5. SSE lines may end with LF, CRLF or CR (also a CRLF split across reads); comments and unknown fields are ignored; an event counts only at its blank line, so an event cut off by a broken connection is never used.
6. As soon as the outcome event arrives the page stops reading (it does not wait for the server to close).

## 3. Answer states

| State | When | What the user sees |
|-------|------|--------------------|
| `idle` | nothing running | Send button; the form has `data-state="idle"` |
| `connecting` | request sent, `started` not yet received | new AI bubble with "กำลังเชื่อมต่อ AI…" and dots; **Stop** replaces Send |
| `streaming` | after `started`; each `delta` appends text | text grows in the **same** bubble (one text node, `appendData`), blinking caret, "AI กำลังพิมพ์คำตอบ…", model chip updated |
| `completed` | `completed` after a non-blank answer | status line removed; Send back |
| `cancelled` | Stop button, Esc, leaving the page (or a server `cancelled` event) | text received so far is kept, dashed border, "หยุดการสร้างคำตอบแล้ว"; with no text yet, the empty bubble is replaced by a small note |
| `error` | `error` event, HTTP/network error, time limit, or the stream ends without an outcome | text received so far is kept and marked "คำตอบไม่สมบูรณ์"; an error bubble shows the Thai text for the code; the question is put back in the input (if empty) |

Rules enforced by `createStreamSession()` (contract BE-004):

- `started` must come first; `delta` before it, `started` twice, or data that is not a JSON object -> `AI_INVALID_RESPONSE`.
- `delta.text` is appended exactly as received (no trimming or normalisation).
- `completed` is accepted only after `started` and a non-blank answer; otherwise `AI_INVALID_RESPONSE`.
- **Exactly one outcome**: after `completed`, `error` or `cancelled`, later events change nothing.
- **The connection ending without an outcome is never "completed"**: `STREAM_INTERRUPTED` ("การเชื่อมต่อขาดก่อนที่คำตอบจะเสร็จ คำตอบที่เห็นอาจไม่ครบ กรุณาลองใหม่").
- Error codes map to the existing Thai texts (`AI_UNAVAILABLE`, `AI_TIMEOUT`, `AI_PROVIDER_ERROR`, `AI_INVALID_RESPONSE`, `AI_RESPONSE_TOO_LARGE`, `INTERNAL_ERROR`); unknown codes fall back to `INTERNAL_ERROR`; the server's `message` text is never shown.
- Unknown event names are ignored (forward compatible).

## 4. Stop, time limit, page leave

- **Stop** (button or **Esc**) calls `abort()` on the request: the browser closes the connection, the backend cancels the Ollama call (verified in both logs, see the verification report). Stop is never shown as an error.
- **Browser time limit**: the server's `TimeoutSeconds + 20` (rendered into the page, FE-001/BE-003). When it fires the request is aborted and shown as `AI_TIMEOUT`, not as a user stop.
- **Leaving the page** (`pagehide`) aborts the running answer, so the server stops generating.
- One answer at a time: while an answer runs, Enter and Send are blocked; a new question can be typed meanwhile. The reset to `idle` happens in a `finally`, so the page cannot get stuck if rendering throws.

## 5. Safety and messages from different requests

- All text is written with `textContent` or a text node; markup in an answer is displayed as text (tested in Node and in the browser).
- Each question has its own session object and bubble; updates are applied only while that request is the active one, so text from one request can never land in another bubble.
- Nothing is stored in the browser (no `localStorage`, cookies, etc.), as in FE-001.

## 6. Scrolling

New text scrolls the conversation only if the reader is already at the bottom (within 48 px). If they scrolled up to read, the page does not jump.
