# BE-003 D5 - Verification report (before and after)

Issue: #16 · Date: 2026-10-20 · Branch: `watzapon04_Backend` · Machine: Windows 11, RTX 4050 Laptop (6 GB), Ollama + `qwen3:4b`
Root cause and evidence: [BE-003-ROOT-CAUSE.md](BE-003-ROOT-CAUSE.md) · Settings and policy: [BE-003-CONFIGURATION.md](BE-003-CONFIGURATION.md)

## 1. What changed

| Area | Change |
|------|--------|
| Response length | `num_predict` is now `Ollama:MaxOutputTokens` (default **1024**, was a fixed 256). `done_reason = "length"` is **`502 AI_RESPONSE_TOO_LARGE`**, no longer `AI_INVALID_RESPONSE` |
| Response size | the body is read in chunks and stops at `Ollama:MaxResponseBytes` (default 1 MiB, checked against `Content-Length` first): `AI_RESPONSE_TOO_LARGE` |
| Malformed responses | still `AI_INVALID_RESPONSE`, now with a bounded `Reason` (11 categories) in the log |
| Timeout | one deadline for the whole exchange in `OllamaAIService` (`Ollama:TimeoutSeconds`); `HttpClient.Timeout` is infinite on purpose |
| Cancellation | covers reading the body; a caller that already left never costs Ollama work; a success is never returned to a caller that left; the response is always released |
| Browser | the wait limit is the server's `TimeoutSeconds + 20`, rendered into the page (was a fixed 200 s); new Thai text for `AI_RESPONSE_TOO_LARGE` |
| Logging | one structured entry per outcome (event ids 4001-4007) with numbers and categories only; the question and the answer are never logged |
| Unchanged | `POST /api/chat` request and success response; error codes and statuses other than the new one; loopback only; AI-002 request fields and success conditions |

## 2. Before / after with the real Ollama (`qwen3:4b`)

| Check | Before (`main` a484f2e) | After (this branch) |
|-------|-------------------------|---------------------|
| Long-answer question through `POST /api/chat`, 6 runs | **502 `AI_INVALID_RESPONSE` x 6** (3.3 - 4.5 s); log: "did not meet the success conditions" | **200 x 6** (2.4 - 11 s, the first includes loading the model); log shows `EvalTokens=279` (more than the old limit of 256), `MaxOutputTokens=1024` |
| An answer that really is too long (`MaxOutputTokens=32`) | not possible to tell apart | `502 AI_RESPONSE_TOO_LARGE` in 1.0 s; log `Reason=OutputTokenLimit EvalTokens=32 MaxOutputTokens=32` |
| Deadline (`TimeoutSeconds=2`) while Ollama is busy with a 1024-token generation | (same behaviour) | `504 AI_TIMEOUT` after 2.1 s; log `TimeoutSeconds=2 ElapsedMs=2031` |
| 5 clients leave after 1 s during a long generation | next request 0.2 s | next request 0.5 s; 5 Information log lines, **0 warnings** |
| 10 simultaneous 3-sentence answers (no limiter exists) | 26.0 s total, all 200 | 24.3 s total, all 200 |
| 30 simultaneous short questions | all 200, max 5.6 s | all 200, max 4.6 s |
| 15 simultaneous with `TimeoutSeconds=12` | 4 x 200, 11 x 504, immediate recovery | 4 x 200, 11 x 504, immediate recovery (0.7 s) |
| Cost of the larger limit: a generation that uses all 1024 tokens | - | 13.2 s at 77 tokens/s (measured directly on Ollama) |
| Chat page with a too-large answer | generic wrong text for the unknown situation | the new text, no AI bubble, the question is put back in the input (`be-003/ui-response-too-large.jpg`) |
| Failure categories visible in the log | none | `Reason` for every invalid / too-large response |

![Too large in the Chat page](be-003/ui-response-too-large.jpg)

## 3. Automated tests

| Suite | Before | After |
|-------|-------:|------:|
| `dotnet test` (xUnit) | 157 | **276**, all passing, 0 warnings |
| `node --test "tests/ui/*.test.mjs"` | 18 | **23**, all passing |

Five new .NET test classes (116 tests; the suite grew by 119 in total, the rest are updated or added tests in existing classes): `OllamaResponseLengthTests` (33: length vs malformed, 20,000-character Thai answers in raw and escaped form, emoji / combining marks / surrogates,
exact size limit +/- 1 byte, declared and endless bodies, `num_predict` from configuration), `OllamaTimeoutCancellationTests` (20: deadline for waiting, for reading the body and for the whole exchange with a
slow-drip body, no early timeout, cancel while waiting and while reading, no success to a caller that left, already cancelled caller, the response is released on 7 paths, connection reset),
`OllamaLoggingTests` (21: every outcome logs one structured entry with the right fields; the question and the answer never appear in any log, including timeout and cancel; stable event ids),
`OllamaResponseLimitsApiTests` (12: HTTP boundary with a fake Ollama: the new error contract in Development and Production, oversized body, long Thai answer, configuration reaches Ollama, regression of the existing codes),
`OllamaLimitOptionsTests` (30: ranges, messages, startup failure, shipped values), plus page tests for the new text and the server-rendered wait limit.

### Mutation check (do the tests catch real bugs?)

Each deliberate bug below was put into a throw-away copy; each makes at least one test fail:
`done_reason=length` treated as malformed again (6 tests) · `Content-Length` not checked (1) · no limit while streaming (3) · no deadline (6) · a client cancellation reported as a timeout (7) ·
success returned to a caller that left (1) · provider called for an already cancelled caller (1) · `num_predict` fixed at 256 again (8) · response never disposed (19) ·
the question written to the log on the timeout path (1) and on the cancel path (1) · a connection reset not mapped (1) · default output limit back to 256 (5) · token range not validated (11) · default body limit raised to 1 GiB (17).
Weak spots found this way were fixed (a test that hung instead of failing when the deadline was missing, a test that did not exercise the "no success after cancel" check, a log-leak test that did not cover the timeout and cancel paths).

## 4. Defects found by this testing and fixed

1. A caller that had already cancelled still caused a request to Ollama (found by `CallerAlreadyCancelled_NeverCallsTheProvider`). Fixed with an early check.
2. Tests that waited for a timeout had no ceiling of their own and hung when the deadline was missing. All now fail within 10 s.
3. The "no success after cancel" test passed even without the check (the read failed first). It now cancels exactly at the end of the body.
4. The "question never logged" test did not cover the timeout and cancel paths. Added.

## 5. Acceptance criteria (Issue #16, section 8)

| Criterion | Evidence |
|-----------|----------|
| Root cause of `AI_INVALID_RESPONSE` with evidence | `BE-003-ROOT-CAUSE.md` (direct Ollama measurements, code, before runs 6/6) |
| Long answers fixed under a size limit | 6/6 success after; limits `MaxOutputTokens` and `MaxResponseBytes` |
| Thai and Unicode correct | `LongThaiAnswer_*`, `Unicode_*`, `UnicodeEscapesWrittenByHand_*`; real Thai answers |
| Clear timeout policy | `BE-003-CONFIGURATION.md` section 3 |
| Cancellation propagation within what the system supports | tests above; real run: 5 leavers, next request 0.5 s, no warnings |
| Timeout, cancellation and upstream errors told apart | different exceptions, codes and log levels (tested) |
| Error mapping consistent with the API contract | `OllamaResponseLimitsApiTests.ExistingErrorContract_Unchanged` + new code |
| No stack trace or sensitive data to the client | contract tests in Development and Production |
| Safe structured logging | `OllamaLoggingTests` |
| Unit tests for business logic and error handling | see section 3 |
| Integration tests for the Ollama HTTP boundary | `OllamaResponseLimitsApiTests`, `OllamaCancellationApiTests`, `OllamaChatApiTests` |
| Regression of `POST /api/chat` | existing suites unchanged and passing; `live-smoke-test.ps1` and `smoke-test.ps1` |
| Verification before and after | section 2 |
| No breaking change without approval | one additive code (`AI_RESPONSE_TOO_LARGE`, status 502) was **requested by the issue**; the AI-002 `num_predict` default moved from 256 to 1024: **needs the owner's confirmation** |
| No production configuration change | only repository defaults changed; nothing deployed |

## 6. Known limitations that remain

- **AI-004 (benchmark) was not available**, so `TimeoutSeconds` stays 180 and no concurrency limit was added. Ollama still answers one request at a time; the numbers are in `docs/KNOWN-LIMITATIONS.md`. The final policy must be reviewed with AI-004.
- **Contract change to confirm:** AI-002 fixes `num_predict` at 256. It is now a setting with default 1024 (the root cause). The AI Engineer should update `docs/ai/ollama-integration-contract.md` and re-run the contract tests.
- A runaway answer can use the whole limit (about 13 s at the measured speed) and lengthens the queue for others; lower `MaxOutputTokens` if that matters more than long answers.
- An answer that still needs more than the limit is not shown partially (AI-002 forbids partial answers); streaming (BE-004) is the real solution for very long answers.
- Measured on one machine (RTX 4050 6 GB), not the target RTX 4060/4070; no real reverse proxy was in the path, so proxy timeouts are documented but untested.
- Request size is not limited by this issue (a 200 KB question was accepted in BE-002 testing).
- The Python tests of AI-002 were not run (no PowerShell 7 on this machine).
- The long-answer test proves the API now returns `200` instead of `502`. The *content* the model returns for the "write as long as possible" question is a polite refusal
  (the AI-002 system prompt asks for brief answers), so very long answers are not produced in practice. That is answer quality (AI-003) and the system prompt, not the backend.
