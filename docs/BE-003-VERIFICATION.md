# BE-003 D5 - Verification report (before and after)

Issue: #16 · Date: 2026-10-20 · Branch: `watzapon04_Backend` · Ollama + `qwen3:4b`, Windows 11

**Reference machine (project spec):** ASUS TUF Gaming F16, Intel Core 5 210H, 16 GB RAM, NVIDIA RTX 4050 Laptop GPU (6 GB). **Measured on:** Acer Predator PHN16-72, Intel Core i7-14700HX, 16 GB RAM, NVIDIA RTX 4060 Laptop GPU (8 GB), which generates faster (73 - 77 tokens/s against ~60 on the reference, AI-004). Pass/fail results and error codes do not depend on the GPU; times on the reference machine are about 25 % longer, and the times that matter are also given for the reference.

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
| Cost of the larger limit: a generation that uses all 1024 tokens | - 13.2 s at 77 tokens/s on the test machine; **about 17 s on the reference machine** (~60 tokens/s) |
| Chat page with a too-large answer | generic wrong text for the unknown situation | the new text, no AI bubble, the question is put back in the input (`be-003/ui-response-too-large.jpg`) |
| Failure categories visible in the log | none | `Reason` for every invalid / too-large response |

![Too large in the Chat page](be-003/ui-response-too-large.jpg)

## 3. Automated tests

| Suite | Before | After |
|-------|-------:|------:|
| `dotnet test` (xUnit) | 157 | **357**, all passing, 0 warnings (276 before the PR #19 review rounds, +53 credential/path tests, +28 static asset tests in section 5b) |
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

## 5a. Review of PR #19 (owner comment): credentials in `Ollama:BaseUrl`

Finding: event 4004 logged `Ollama:BaseUrl` as written, and the validator accepted `http://user:password@127.0.0.1:11434`, so credentials could reach the application log when Ollama was down. Confirmed as a real defect.

| Fix | Where |
|-----|-------|
| `BaseUrl` with user info, a path other than `/`, a query or a fragment is refused at startup (checked on the parsed `Uri`; round 2 of the review added the path rule); the message states the rule and never repeats the value | `OllamaOptionsValidator` |
| Logs use `Endpoint=scheme://host:port` (`OllamaOptions.DescribeEndpoint`; unparsable value -> `invalid`); the field `BaseUrl` no longer exists in event 4004 | `OllamaLog`, `OllamaAIService` |
| The `HttpClient` base address is scheme + host + port only (`OllamaOptions.BuildBaseAddress`), so the framework's own request log cannot print a path secret even if validation were off | `Program.cs` |

Regression tests (`OllamaBaseUrlSecretsTests`, 45 cases): the validator refuses 7 credential-bearing URLs and 4 query/fragment URLs and still accepts 6 clean loopback URLs; the failure message and the startup
exception never contain the user name or password; `DescribeEndpoint` keeps only scheme, host and port (8 cases); a real failed connection with a credential URL logs only `Endpoint` and no log entry, field or exception text contains the secret;
the whole host (validation switched off on purpose, framework request logging included) never logs the secret. `OllamaLoggingTests` now asserts the `Endpoint` field.
Mutation check: logging the raw URL again (6 tests fail), removing the `@` check (21 fail), a `DescribeEndpoint` that keeps user info (11 fail).

Round 2 (owner comment 6282466768): `http://127.0.0.1:1/REVIEW-PATH-SECRET` passed validation and the built-in HttpClient log printed `.../REVIEW-PATH-SECRET/api/generate`. Confirmed, fixed as above. New tests: 5 path URLs refused, 4 whole-host cases (path, nested path, query, fragment; validation off) show the framework log line without the secret. Now 329 .NET tests. Mutations: no path check (8 fail), base address keeps path (1 fail), both (9 fail). Real app: the owner's URL stops startup; default config answers 200.

Other review points: (c) `num_predict` 256 -> 1024 still **needs the AI-002 owner's confirmation** (section 6); `docs/ai` was not edited. (d) The PR description now says `Refs #16` instead of `Closes #16`, because the final timeout / concurrency policy depends on the AI-004 benchmark.

## 5b. Pre-merge verification (branch with `main` and `pao04_AI` merged)

Checked from a fresh clone of the pushed branch: build 0 warnings; .NET tests in default, hostile-environment and Production settings; Node tests;
all 160 AI-003 evaluation cases against the real Ollama (160/160, judged with the same rules as `scripts/test-chat-e2e.ps1`, re-implemented in Python
because PowerShell 7 was not installed at that point; the real script was run later, section 5c); live validation, 200 KB input, cancellation, 10 concurrent requests; the Chat page in a browser.

Two defects were found in the web page (both already on `main`, not caused by BE-003) and fixed:

| Defect | Cause | Fix | Regression test |
|--------|-------|-----|-----------------|
| With `dotnet run` outside Development the Chat page had **no style and no script**: CSS/JS arrived as empty `200` responses to any browser (browsers send `Accept-Encoding: gzip`) | `MapStaticAssets` answers gzip clients from pre-compressed copies under `obj/`, which are found only through the static web assets manifest, and ASP.NET Core loads that manifest in Development only | `builder.WebHost.UseStaticWebAssets()` in `Program.cs` (documented for running from build output outside Development). A published app has no such manifest and is unchanged (checked with and without the fix) | `StaticAssetsTests` (24 cases: Development/Production x 4 encodings x 3 files, decoded content compared with the file); removing the fix makes 8 fail |
| The Error page (`_Layout`) linked `~/BC_LocalAI.styles.css` -> **404**; the scoped CSS bundle is named after the assembly, `BC-LocalAI.styles.css` | template name with `_` instead of `-` | link corrected | `EveryLocalLinkOnThePage_Resolves_AndIsNotEmpty` (every local `href`/`src` on `/` and `/Home/Error`, both environments) |

Not changed, documented in `README.md`: a **published** copy started from another directory has no style or script because the content root (where `wwwroot` is looked for)
is the current directory. Start it from its own folder or pass `--contentRoot`.

## 5c. With PowerShell 7 installed (scripts of AI-002, AI-003 and AI-004 run for real)

| Check | Result |
|-------|--------|
| Python tests (`tests/`, need `pwsh`) | first run **40/45**, 41 of which had been skipped before; after the fixes below **45/45**, nothing skipped |
| AI-002 `scripts/test-ollama.ps1`, 3 contract prompts + unavailable port | 3 x exit 0 (`done_reason=stop`); port 11435 -> exit 1, as the contract requires |
| AI-002 owner's evidence `codex/ai002-1024-evidence` (merged) and its `test-ollama-1024.ps1`, re-run here | 3 x exit 0, requests carry `num_predict=1024` |
| AI-003 `scripts/test-chat-e2e.ps1` against the Backend (Production) | **160/160**, warm average 349 ms, P95 434 ms |
| AI-004 benchmark, profile `long`, concurrency 1, 15 rounds (after BE-003) | Ollama direct **15/15** and Backend **15/15** (baseline before BE-003: 80 %). The three Thai prompts that were cut at 256 tokens 105/105 times now need **282 - 375 tokens** and end with `stop`; P50 ~2.9 s, P95 ~6.5 s, 0.28 req/s (unchanged throughput). Not a full re-run of all 24 scenarios |

Defects found by these runs and fixed (AI scripts, needed for the merged branch to be consistent):

| Defect | Fix |
|--------|-----|
| `ollama-benchmark.ps1` still sent `num_predict=256` while the Backend now sends `Ollama:MaxOutputTokens` (1024), so "Direct vs Backend" compared different requests; its test read the value from C# source that BE-003 changed | the script reads `Ollama:MaxOutputTokens` from `appsettings.json` (`-NumPredict` overrides); the test compares with the configured value and checks the override |
| Thai text became `?` when a script's output is redirected on an English Windows (console code page 437): `test-ollama.ps1` output and the prompt-pool test | UTF-8 console output in `test-ollama.ps1` and in that test |

**AI-002 `num_predict`:** the AI-002 owner tested 1024 live (3/3, evidence in `docs/ai/evidence/ai002-num-predict-1024/`). That evidence covers short answers only; the contract document still states 256 and is the owner's to update.

## 5d. Cancellation and the 1024-token limit, checked against Ollama's own log and token counts

Times in this section are from the test machine (RTX 4060 Laptop); on the reference machine (RTX 4050 Laptop) expect about 25 % longer. Both points had only been inferred before (AI-004 handoff item 4; the AI-002 owner's note that answers near 1024 tokens were not tested).

| Check | Result |
|-------|--------|
| Client leaves 2 s into a long generation, model warm | Backend logs "cancelled by the caller" (1.9 s); **Ollama's log shows `stop: cancel task` at 1.99 s**, the next request is answered in 0.3 s. The generation really stops |
| Same while the model is still loading (cold) | Ollama logs `client connection closed before llama-server finished loading, aborting load`; the next request pays the load again (5.0 s). Expected, nothing to fix |
| Answer just under the limit: numbers 1-250 | **200**, complete list, `EvalTokens=900`, 13.8 s |
| Answers over the limit: numbers 1-300, numbers 1-1500 | `502 AI_RESPONSE_TOO_LARGE`, log `Reason=OutputTokenLimit EvalTokens=1024`, 15.6 / 16.5 s |
| Realistic Thai question: "list all 77 provinces of Thailand" | `502 AI_RESPONSE_TOO_LARGE` (`EvalTokens=1024`, 17.2 s). Measured directly with `num_predict=4096`: the full answer needs **1,077 tokens** (`stop`, no repetition) |

Token needs measured directly on Ollama (`num_predict=4096`, same system prompt and format): numbers 1-150 = 500 tokens, 1-300 = 1,100 tokens, the 77 provinces = 1,077 tokens.

**Open sizing decision:** 1024 fits every answer measured in BE-003 and AI-004 (longest 375 tokens for the AI-004 Thai prompts), but a realistic Thai listing question needs about 5 % more. Raising `Ollama:MaxOutputTokens` (for example to 2048) would let such answers through, at the cost of a longer worst case (about **34 s on the reference machine**, 27 s on the test machine) and a longer queue for others. Not changed here: 1024 is the value the AI-002 owner has just tested, and the choice belongs to the Tech Lead / AI-002 owner.

## 6. Known limitations that remain

- **AI-004 results were compared afterwards** (`BE-003-CONFIGURATION.md` sections 2a/2b): they give no reason to change `TimeoutSeconds = 180` (no timeout in 3,156 requests, longest 28.7 s). **No concurrency limit / bounded queue was added**: AI-004 recommends one, but it is a contract change that needs approval. Ollama still answers one request at a time. The BE-003 build was **not re-benchmarked** with the AI-004 scripts (they need PowerShell 7).
- **Contract document to update:** the AI-002 owner tested `num_predict` 1024 live (short answers, section 5c), but `docs/ai/ollama-integration-contract.md` still fixes it at 256. It is now a setting with default 1024 (the root cause). The AI Engineer should update `docs/ai/ollama-integration-contract.md` and re-run the contract tests.
- A runaway answer can use the whole limit (about 17 s on the reference machine, about 13 s on the faster test machine) and lengthens the queue for others; lower `MaxOutputTokens` if that matters more than long answers.
- An answer that still needs more than the limit is not shown partially (AI-002 forbids partial answers); streaming (BE-004) is the real solution for very long answers.
- Measured on a faster machine than the reference (RTX 4060 Laptop 8 GB against the reference RTX 4050 Laptop 6 GB; earlier versions of these documents wrongly named the test machine RTX 4050). Functional results carry over; times on the reference machine are about 25 % longer (AI-004 measured the reference: ~60 tokens/s, `qwen3:4b` uses 3.2 of 6 GB VRAM); no real reverse proxy was in the path, so proxy timeouts are documented but untested.
- Request size is not limited by this issue (a 200 KB question was accepted in BE-002 testing).
- The full AI-004 benchmark (24 scenarios, 125+ requests each) was not repeated after BE-003; only the `long` profile at concurrency 1 (section 5c).
- The long-answer test proves the API now returns `200` instead of `502`. The *content* the model returns for the "write as long as possible" question is a polite refusal
  (the AI-002 system prompt asks for brief answers), so very long answers are not produced in practice. That is answer quality (AI-003) and the system prompt, not the backend.
