# BE-004: Streaming Chat API Contract

## Endpoint

`POST /api/chat/stream`

Request body:

```json
{ "message": "อธิบาย Local AI แบบสั้น ๆ" }
```

Blank, missing, malformed or wrong-type input returns HTTP 400 with the existing JSON `INVALID_MESSAGE` contract before SSE starts.
A valid request returns HTTP 200 with:

```text
Content-Type: text/event-stream; charset=utf-8
Cache-Control: no-cache
X-Accel-Buffering: no
```

## Events

Each event uses `event: <name>` followed by a single JSON `data:` line and a blank line.

| Event | Data | Rule |
|---|---|---|
| `started` | `{"model":"qwen3:4b"}` | Sent and flushed before waiting for Ollama's first record. |
| `delta` | `{"text":"..."}` | Decoded text from the `answer` value only. Concatenate in order without normalization. |
| `completed` | `{"finishReason":"stop","evalCount":7}` | Sent only after `done:true`, `done_reason:"stop"`, a complete non-blank answer and a valid stream. |
| `error` | `{"code":"AI_...","message":"...","partial":true}` | Terminal failure. `partial` is true after at least one delta. Provider detail is never exposed. |
| `cancelled` | `{}` | Best-effort on caller cancellation with a separate 1-second delivery budget. Client disconnect normally makes the connection unwritable, so delivery is not guaranteed. |

There is exactly one terminal outcome: `completed`, `error`, or a client disconnect/cancellation. `completed` is never sent after an error.

## Error mapping after SSE has started

| Condition | Code |
|---|---|
| Ollama cannot be reached | `AI_UNAVAILABLE` |
| Total deadline expires | `AI_TIMEOUT` |
| Ollama HTTP error or `{"error":...}` record | `AI_PROVIDER_ERROR` |
| Invalid NDJSON, wrong model, interrupted stream, invalid answer JSON, unexpected finish reason | `AI_INVALID_RESPONSE` |
| `done_reason:"length"` or response byte limit | `AI_RESPONSE_TOO_LARGE` |
| Unexpected backend failure | `INTERNAL_ERROR` |

HTTP remains 200 after `started`; the error is represented by the terminal SSE event.

## Ollama contract

The backend calls loopback-only `POST /api/generate` using `stream:true`, `think:false`, the AI-002 JSON schema and configured
`num_predict`. NDJSON is read line-by-line with a stateful UTF-8 decoder. The `answer` JSON value is decoded incrementally;
JSON scaffolding and the `thinking` field are never forwarded. Unknown Ollama fields are ignored, while malformed known fields fail closed.

The decoder preserves Unicode and JSON escapes even when UTF-8 code points, NDJSON records, the `answer` key or escape sequences cross reads.
It retains a short tail so `<think>` and `</think>` split across records are rejected before the marker is emitted.

Upstream must return HTTP 200, `application/x-ndjson`, and UTF-8 (charset may be omitted). Other HTTP status codes map to
`AI_PROVIDER_ERROR`; incorrect media types/charsets map to `AI_INVALID_RESPONSE`. Known fields must have the documented types,
duplicate record fields are rejected, and `response` may be omitted (treated as empty) per AI-005. An explicitly null `response` is invalid.
Only JSON whitespace is accepted around the answer envelope. Decoded output is grouped per upstream fragment, with a final tail flush.

Downstream write failures abort processing and close upstream. A failure writing a terminal event never triggers another terminal event.

Reference: [AI-005 contract](ai/ollama-streaming-contract.md).
