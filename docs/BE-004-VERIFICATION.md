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
| `dotnet test ... -c Release --no-build --no-restore` | 416 passed, 0 failed (final review run) |
| Same command with `ASPNETCORE_ENVIRONMENT=Production` | 416 passed, 0 failed (final review run) |
| Streaming-focused .NET tests | 59 included in the full suite (39 regression/transport/controller cases added during review) |
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
- Real-Ollama cancellation is now exercised by closing the downstream response after the first delta, then successfully sending a recovery request.
  No runner process was killed. Controlled Kestrel provider tests additionally assert upstream `RequestAborted` on disconnect/timeout.

## Review follow-up and expanded live verification

[Review findings and fixes](BE-004-REVIEW-FIXES.md) document the 10 red regression cases and subsequent corrections.
The final build has 0 warnings/errors; final Development and Production suites both pass 416/416.
The unchanged FE-001 UI suite passes 23/23. A large-fragment regression confirms 1,000 answer characters need 2 deltas instead of 1,000.

Live checks with Ollama 0.40.6 and `qwen3:4b`:

| Verification | Actual result |
|---|---|
| `scripts/test-chat-stream.py --output <scratch>.json` | 8/8 checks passed on final code: Thai, English, emoji/multiline, two simultaneous isolated answers, cancellation, recovery and legacy API |
| Same script against a separate instance with `Ollama__MaxOutputTokens=16`, `--expect-limit` | 1/1 passed; partial answer followed by `error AI_RESPONSE_TOO_LARGE`, `partial:true`, no completed |
| `scripts/test-ollama.ps1 -OutputPath <scratch>.json` | AI-002 smoke passed with HTTP 200, stop, valid JSON answer |
| `scripts/test-chat-e2e.ps1 -BaseUrl http://127.0.0.1:5290 -OutputPath <scratch>.json` | 160/160 passed; warm average 347.3 ms, p95 442 ms |
| Long non-streaming regression, concurrency 1, 15 measured rounds | 15/15 succeeded, 0 timeout/invalid/truncated; p50 2851.8 ms, p95 6494.7 ms; 4 additional warmups succeeded |
| Browser: Thai question through FE-001 | Answered `สวัสดี! หวังว่าคุณจะมีวันที่ดีนะ 😊`; send control re-enabled |

Evidence: [live stream JSON](be004/live-stream-review.json), [output limit JSON](be004/live-output-limit.json),
[browser screenshot](be004/browser-thai.png). The repeatable script is `scripts/test-chat-stream.py`; output paths point to scratch files
so existing AI/performance baselines are not overwritten.

Timings above were measured on the development i7-14700HX / RTX 4060 Laptop 8 GB machine, not the reference
Core 5 210H / RTX 4050 Laptop 6 GB machine. Applying the prior baseline's approximate 25% difference would estimate
long-response p50 around 3.6 s and p95 around 8.1 s on the reference machine; these are estimates, not measurements or SLA.
The 15-sample benchmark is preliminary; its p99 is not reliable. Benchmark host sampling saw available RAM drop to 227 MB,
so these timings include host resource pressure and must not be treated as clean hardware capacity measurements.

The original implementation had 377 .NET tests; this review expanded it to 416. All raw live prompts are synthetic and contain no credentials.
Independent reviewer approval is still pending. No frontend streaming implementation, authentication, history or deployment was added.

The final full Python rerun passed 72/72 in 175.149 seconds. The two earlier transient failures and isolated passing reruns remain
documented in [Review fixes](BE-004-REVIEW-FIXES.md); no AI-owned script or baseline was modified to obtain a pass.
The existing benchmark tests still emit Python ResourceWarning messages about CSV handles; these did not fail the final suite.
