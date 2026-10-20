# BE-004 Verification Report

Date: 2026-10-20

Branch: `watzapon_BE-004`

Base after pull: `origin/main` at `0cff336` (AI-005 merged by PR #26)

## Implemented

- Added `POST /api/chat/stream` with `started`, `delta`, `completed` and safe `error` SSE events.
- Added native Ollama NDJSON streaming with incremental JSON-answer decoding.
- Preserved split UTF-8, Thai, emoji and JSON escapes.
- Rejected wrong model, invalid records, data after completion, interrupted streams, empty/invalid answers and thinking markers.
- Ignored the upstream `thinking` field and unknown fields.
- Added total timeout, response byte limit and client cancellation propagation.
- Configured immediate upstream close on cancellation with `MaxResponseDrainSize = 0`.
- Preserved the existing non-streaming API.

## Automated verification actually run

| Command | Result |
|---|---|
| `dotnet build ... -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test ... -c Release --no-build --no-restore` | 377 passed, 0 failed |
| Same command with `ASPNETCORE_ENVIRONMENT=Production` | 377 passed, 0 failed |
| Streaming-focused .NET tests | 20 added and included in the full suite |
| `node --test "tests/ui/*.test.mjs"` | 23 passed, 0 failed |
| `python -I -X utf8 -m unittest discover -s tests -p "test_*.py"` | 72 passed, 0 failed (run outside sandbox because tests create fake loopback servers) |

The first Python run inside the sandbox failed because Windows denied local socket creation. The identical command outside the sandbox
passed 72/72; the sandbox failure is retained here rather than being represented as a product failure.

## Real Ollama verification

Environment: local Ollama with installed `qwen3:4b`; API in Production at `http://127.0.0.1:5290`.

Request: `หนึ่งบวกหนึ่งเท่ากับเท่าไร ตอบสั้น ๆ เป็นภาษาไทย`

Observed stream:

```text
event: started
data: {"model":"qwen3:4b"}

event: delta
data: {"text":"2"}

event: completed
data: {"finishReason":"stop","evalCount":7}
```

The existing `/api/chat` returned `{"response":"2","model":"qwen3:4b"}`. The cold streaming request completed in about 5.3 seconds;
the following warm non-streaming request completed in about 0.27 seconds. No prompt or answer appeared in structured application logs.

## Mutation check

Temporarily removed the `record.Model == configured model` validation and ran `WrongOrMissingModel_IsInvalid`. Both cases failed with
“No exception was thrown”, proving the tests detect this regression. Validation was restored, the same two cases passed, and the project
was rebuilt. The mutation was not committed.

## Remaining limitations

- The current Web UI still uses `/api/chat`; adopting streaming belongs to FE-002.
- Reverse-proxy buffering has not been exercised.
- Only a total timeout is configured; separate first-record and idle timeouts await a Tech Lead decision.
- A `cancelled` event is best-effort because a disconnected client normally cannot receive it; cancellation still reaches Ollama.
- Real-Ollama cancellation was not destructively injected during this run; cancellation propagation is verified with controlled streams,
  while AI-005 supplies the real Ollama cancellation evidence.
