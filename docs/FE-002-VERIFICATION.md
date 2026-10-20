# FE-002 - UI verification report (D4)

Issue: #18 · Date: 2026-10-20 · Branch: `watzapon_FE-002` (from `main` a983b80, which contains BE-004)
Reference machine (project spec): ASUS TUF Gaming F16, Core 5 210H, 16 GB, RTX 4050 Laptop 6 GB. Measured on: Acer Predator PHN16-72,
i7-14700HX, 16 GB, RTX 4060 Laptop 8 GB (faster). Browser: Chromium (built-in browser of the Claude desktop app), desktop and 375 x 812 phone emulation.
Ollama + `qwen3:4b`, backend run as `ASPNETCORE_ENVIRONMENT=Production`.

## 1. Automated tests

| Suite | Before (main a983b80) | After |
|-------|------:|------:|
| `dotnet test` (xUnit), Development and Production | 416 | **428**, all passing, 0 warnings |
| `node --test "tests/ui/*.test.mjs"` | 23 | **61** (38 new in `tests/ui/chat-stream.test.mjs`), all passing |
| `python -m unittest discover -s tests` (needs PowerShell 7) | not re-run on main | **72**, all passing (FE-002 changes no file these tests cover) |

New and changed tests:

- `chat-stream.test.mjs` (38): SSE parser (event split at **every** character position and one character at a time, LF / CRLF / CR,
  CRLF split across reads, comments and unknown fields, multi-line data, an event cut off before its blank line is never delivered);
  answer session (normal path, text unchanged incl. Thai / emoji / combining marks / whitespace, markup kept as text, 9 contract
  violations -> `AI_INVALID_RESPONSE`, every BE-004 error code -> its Thai text, unknown codes -> `INTERNAL_ERROR`, exactly one outcome,
  `cancelled` event, unknown events ignored, end without outcome -> `STREAM_INTERRUPTED`, Stop keeps text, sessions share nothing);
  `readAnswer` with real `Response` / `ReadableStream` (states in order, Thai and emoji delivered **byte by byte**, HTTP 400 JSON contract,
  non-JSON HTTP error, non-SSE 200, connection closed early, cut-off `completed`, network failure mid-stream, Stop vs browser time limit,
  returns at the outcome even if the server keeps the connection open, two streams at the same time never mix).
- `ChatPageTests`: the page loads `chat-core.js` -> `chat-stream.js` -> `chat.js`; `chat.js` makes exactly one `fetch`, to
  `/api/chat/stream`, with the abort signal, never `EventSource` / `XMLHttpRequest`; streamed text goes into a text node; one answer at
  a time and the reset to idle is in `finally`; the Stop button exists, is a `type="button"`, starts hidden; the form starts `idle`;
  `chat-stream.js` is included in the no-CDN / no-markup / no-storage / no-`eval` checks.
- `StaticAssetsTests`: `chat-stream.js` arrives complete for every encoding in Development and Production.

### Mutation check

Each deliberate bug in `chat-stream.js` makes at least one test fail: `completed` allowed with an empty answer (2) · an interrupted
stream counted as completed (3) · events after the outcome still applied (1) · non-streaming UTF-8 decoding, which breaks Thai split
across reads (1) · `delta` accepted before `started` (1) · Stop reported as an error (1) · a CRLF split across reads counted as two line
ends (1) · not stopping at the outcome while the server keeps the connection open (1).

### Defects found by these tests and fixed

1. After a completed answer the final state was reported twice (a duplicate UI update) - found by the "states in order" test.
2. In the first version of `chat.js` the page went back to "ready" only after the `try` block, so an exception while rendering would have
   left it stuck in "generating" with no Send button - found by the FE-001 page test that requires the reset in `finally`.

## 2. Browser, real backend and real Ollama

| Scenario | Result |
|----------|--------|
| Thai question, normal answer | states **idle -> connecting -> streaming -> completed -> idle** (recorded with a DOM observer); the text grew in the **same bubble in 133 steps**; Stop visible only while generating; status line removed at the end; model chip `qwen3:4b` (`streaming-in-progress.jpg`, `streaming-completed.jpg`) |
| **Stop button** during a long answer | state `cancelled`; the 327 characters received stay, dashed border, "หยุดการสร้างคำตอบแล้ว"; Send back; no error. Backend log: `Request cancelled by the caller; the Ollama call was stopped`; **Ollama log: `stop: cancel task`** |
| **Esc** before any text arrived | cancelled; the empty bubble is replaced by the note "หยุดการสร้างคำตอบแล้ว" |
| Backend deadline (Ollama not answering, see section 4) | after 180 s: "AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่", empty bubble removed, question put back, page idle (`stopped-and-timeout.jpg`) |
| Server not running | "ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ …", question put back, Send available |
| Phone width 375 px while streaming | Stop button fits, no horizontal scroll (`mobile-streaming.jpg`) |

## 3. Browser with a mock SSE stream (`fetch` replaced inside the page; allowed by the issue for cases a real model cannot produce on demand)

| Scenario | Result |
|----------|--------|
| Answer containing `<img onerror=…>` and `<script>` | shown as plain text; no element created, nothing executed |
| Stream ends after partial text, no outcome | partial text kept, "คำตอบไม่สมบูรณ์", error "การเชื่อมต่อขาดก่อนที่คำตอบจะเสร็จ …", question put back |
| `error` (`AI_RESPONSE_TOO_LARGE`) after partial text | partial text kept and marked incomplete; Thai text for the code; server message not shown |
| Network failure mid-stream | same as the interrupted case |
| `200` that is not `text/event-stream` | "ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่" |
| Two questions in a row | "A" and "B" in separate bubbles, nothing mixed |
| Reader scrolls up while text arrives | the view stays where the reader is; at the bottom it follows the new text |
| Request sent | `POST /api/chat/stream`, body `{"message": …}` |

## 4. Finding outside FE-002: Ollama hung once after a cancellation

After a Stop and then an Esc 3 s into the next request, Ollama logged `aborting completion request due to client closing the connection`
and then stopped answering **every** request, including direct calls to `:11434` without the app (two direct calls timed out after 20-25 s).
`ollama stop qwen3:4b` (unload) brought it back. The UI behaved correctly (backend `AI_TIMEOUT` after 180 s).
It could **not be reproduced** in 7 further attempts (3 direct `curl` cancellations, 4 through `/api/chat/stream`, different timings).
Recorded for the AI team (Ollama 0.40.5). AI-005 proposed a first-record / idle timeout for streaming that BE-004 did not add; it would
end such a wait sooner than 180 s - a backend decision, not part of FE-002.

## 5. Acceptance criteria (Issue #18, section 10)

| Criterion | Evidence |
|-----------|----------|
| Connects to `POST /api/chat/stream` | page tests; browser runs |
| Shows the answer continuously | 133 updates of one bubble during one answer |
| Supports the BE-004 SSE event contract | `createStreamSession` rules; contract tests in Node |
| Loading / Streaming / Completed / Error states | `data-state` on form and bubble; recorded sequence; status line texts |
| Stop Generating button | `#stop`; real run with backend and Ollama logs |
| AbortController cancels the request | one `AbortController` per question; Ollama `stop: cancel task` |
| No duplicate text, nothing across requests | text node `appendData`; per-request session; two-streams test; sequential mock run |
| Thai, English, Unicode | byte-by-byte Thai/emoji test; real Thai answers |
| Partial response handled | kept and marked "คำตอบไม่สมบูรณ์" / "หยุดการสร้างคำตอบแล้ว" |
| Stream interrupted handled | `STREAM_INTERRUPTED` (Node tests, mock browser run) |
| No "completed" when the stream is incomplete | end without outcome, cut-off `completed`, empty `completed` -> never completed (tests + mutations) |
| Auto scroll that does not disturb | follows only within 48 px of the bottom (browser check) |
| XSS protection | text nodes / `textContent` only; markup test in Node and browser; page checks forbid `innerHTML` etc. |
| Browsers in scope | Chromium desktop and phone emulation; others not tested (section 6) |
| Automated tests | section 1 |
| UI verification report | this document |
| Existing chat features still work | Enter / Shift+Enter, empty-message hint, Thai error texts, question put back, model chip, CSP, no storage: FE-001 page tests unchanged or updated to the same intent, all passing |

## 6. Limitations

- Tested in Chromium only (desktop and phone emulation). Firefox and Safari were not tested; the code uses only standard `fetch`,
  `ReadableStream`, `TextDecoder` and `AbortController`.
- A server `cancelled` event is handled but rarely seen: after Stop the browser has already closed the connection (BE-004 contract).
- Time to first text was not measured as a benchmark.
- The browser time limit is the server deadline + 20 s (FE-001/BE-003); there is no separate "no new text for N seconds" limit (see section 4).
