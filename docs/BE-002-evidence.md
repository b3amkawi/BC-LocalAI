# BE-002 Verification Evidence

- Date: 2026-10-19 (Asia/Bangkok)
- Branch: `watzapon01_Backend`
- Scope: GitHub Issue #6 — Integrate ASP.NET Chat API with Ollama
- Runtime: .NET 10, Ollama with `qwen3:4b` (loopback `127.0.0.1:11434`)
- Provider contract used: Issue #5 (AI-002), section 4 and 5

## 1. What was implemented

```
Web UI -> POST /api/chat -> ChatController -> IAIService -> OllamaAIService -> POST /api/generate -> qwen3:4b
```

| Deliverable | Location |
|-------------|----------|
| `OllamaAIService` (typed `HttpClient`, timeout, cancellation, response validation) | `BC-LocalAI/Services/OllamaAIService.cs` |
| Ollama request/response DTOs (separate from public DTOs) | `BC-LocalAI/Models/Ollama/` |
| Options + startup validation | `BC-LocalAI/Configuration/OllamaOptions.cs`, `appsettings.json` |
| Provider error type and mapping | `BC-LocalAI/Services/AiServiceException.cs`, `Middleware/ExceptionHandlingMiddleware.cs` |
| `HttpClient` registration, `ValidateOnStart` | `BC-LocalAI/Program.cs` |
| Unit tests | `BC-LocalAI.Tests/OllamaAIServiceTests.cs`, `OllamaOptionsValidatorTests.cs` |
| Integration tests (fake HTTP handler, no Ollama) | `BC-LocalAI.Tests/OllamaChatApiTests.cs`, `ChatApiIntegrationTests.cs`, `ChatApiProductionErrorTests.cs` |
| Live smoke test | `scripts/live-smoke-test.ps1` |

Design notes:

- `ChatController` is unchanged except for response-type annotations; it still only calls `IAIService`.
- No silent fallback to `MockAIService`. Tests replace `IAIService` explicitly.
- Prompts and answers are never logged. Provider errors are logged without bodies.
- No new NuGet packages in the app (only .NET built-in APIs).
- Success conditions follow Issue #5: `model` equals the configured model, `done` is boolean `true`,
  `done_reason` is `stop`, `response` is a JSON object with exactly one non-empty string field `answer`,
  and the answer contains no `<think>` / `</think>`.

## 2. Build and automated tests

```text
dotnet build .\BC-LocalAI.slnx  ->  0 Warning(s), 0 Error(s)
dotnet test  .\BC-LocalAI.slnx  ->  Passed! Failed: 0, Passed: 105, Skipped: 0, Total: 105
```

No test needs Ollama: provider behaviour is simulated with a fake `HttpMessageHandler`.
The test run is stable across repeated runs and independent of the machine: tests pin their own Ollama
configuration, so the result is the same with no `Ollama__*` environment variables, with different valid
ones, or with invalid ones set.

### Mutation check (do the tests catch real bugs?)

Each deliberate bug was applied to a throw-away copy of the code; the tests must fail. Result: every one is caught.

| Deliberate bug | Caught |
|----------------|--------|
| `done_reason` check removed | yes |
| `<think>` / `</think>` check removed | yes |
| extra fields in `answer` JSON allowed | yes |
| timeout mapped to `AI_UNAVAILABLE` | yes |
| caller `CancellationToken` not forwarded | yes (test finishes in 5 s, not at HttpClient's 100 s timeout) |
| model hardcoded instead of configuration | yes |
| model equality check removed | yes |
| provider exception details leaked to the user | yes (11 tests) |
| `stream` set to true / `think` set to true | yes |
| startup configuration validation removed | yes |
| silent fallback to `MockAIService` | yes (13 tests) |

Three of these initially survived (a lone `<think>`, a dropped token, a hardcoded request model);
tests were tightened until all were caught.

### Defect found and fixed during the final audit

A provider response declaring an unknown `charset` made `ReadFromJsonAsync` throw `InvalidOperationException`, which was
not handled, so the user received `500 INTERNAL_ERROR` instead of `502 AI_INVALID_RESPONSE` (Issue #6 section 7: malformed
response). `OllamaAIService` now maps any failure while reading the response body (other than cancellation) to
`AI_INVALID_RESPONSE`. Test: `InvalidResponse_WhenOkResponseDeclaresUnsupportedCharset` fails on the old code and passes now.
Also added: non-JSON content types, empty body, emoji / non-BMP prompt, lone surrogate over HTTP.

### Contract alignment with AI-002 (PR #8)

The Ollama contract delivered by AI-002 (`docs/ai/ollama-integration-contract.md` in PR #8, branch `pao02_AI`, not merged yet)
was compared with the implementation. All request fields and the nine success conditions match. The one difference was the
`system` prompt: the AI-002 document asks the model to answer "in the same language as the user ... with the final answer",
which is more specific than the example in Issue #5. The implementation now uses the AI-002 wording. With it, real Thai
questions are answered in Thai (for example "ประเทศไทยมีกี่จังหวัด ตอบสั้น ๆ" -> `77 จังหวัด`) and English questions in English.
AI-002 also asks for a configurable timeout, cancellation support, and re-testing when the prompt changes: all done
(`Ollama:TimeoutSeconds`; live smoke test re-run with the new prompt). It suggests up to 300 s on machines without a GPU or on the
first model load; the default here is 180 s and can be raised through configuration.

## 3. Live smoke test with real Ollama (`qwen3:4b`)

```text
PASS  thai calculation  -> 200 {"response":"2","model":"qwen3:4b"}
PASS  english calculation  -> 200 {"response":"5","model":"qwen3:4b"}
PASS  thai question  -> 200 {"response":"กรุงเทพมหานคร","model":"qwen3:4b"}
PASS  empty message -> 400  -> 400 {"error":{"code":"INVALID_MESSAGE","message":"Message is required"}}
All live smoke tests passed        (exit code 0)
```

The exact command from Issue #6 (`Invoke-RestMethod` with UTF-8 bytes) returned `{"response":"2","model":"qwen3:4b"}`;
the answer is a plain string, not nested JSON.

## 4. Live failure scenarios (real app, `ASPNETCORE_ENVIRONMENT=Production`)

Ollama-down used a closed port; the others used a small fake provider on loopback. All answers were JSON.

| Scenario | Result |
|----------|--------|
| Ollama not reachable | `503 {"error":{"code":"AI_UNAVAILABLE","message":"AI service is unavailable"}}` |
| Ollama never answers (`TimeoutSeconds=2`) | `504 {"error":{"code":"AI_TIMEOUT","message":"AI service timed out"}}` |
| Ollama returns HTTP 500 | `502 {"error":{"code":"AI_PROVIDER_ERROR","message":"AI service returned an error"}}` |
| `done_reason` = `length` (truncated) | `502 AI_INVALID_RESPONSE` |
| Body is not JSON | `502 AI_INVALID_RESPONSE` |
| Body is `{}` (incomplete) | `502 AI_INVALID_RESPONSE` |
| `Ollama__TimeoutSeconds=0` | app refuses to start: `Ollama:TimeoutSeconds must be between 1 and 600.` |

## 4b. Edge cases against the real Ollama (`qwen3:4b`, Production mode)

| Scenario | Result |
|----------|--------|
| `Ollama__Model=model-does-not-exist` (real Ollama answers HTTP 404) | `502 AI_PROVIDER_ERROR` in 0.9 s |
| `Ollama__TimeoutSeconds=1` with a heavier Thai prompt | `504 AI_TIMEOUT` in 1.7 s |
| Thai question asking for a 1500-word essay | `502 AI_INVALID_RESPONSE` in 4.6 s: Ollama stops at `num_predict=256` (`done_reason=length`) and the truncated JSON is rejected, as the contract requires |
| Client disconnects after 1 s during a long generation | handled quietly (information log "Request was cancelled by the client", no error); the next request answered normally (`{"response":"2","model":"qwen3:4b"}`) |
| Ollama listener | `127.0.0.1:11434` only (checked with `Get-NetTCPConnection`); no `OLLAMA_HOST` override set |

Known limit found here: answers longer than 256 tokens cannot be returned (see README "Known limits").
Testing note: send Thai text as UTF-8 (`Invoke-RestMethod` with UTF-8 bytes or `curl --data-binary @file`);
Git Bash `curl -d` on Windows turns Thai into `?`, which makes Ollama answer about question marks.

## 4c. Limits measured with the real Ollama

Full table and caveats: [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md). Highlights: answers over 256 tokens return 502;
Ollama serves requests one at a time (10 simultaneous 3-sentence answers took 26 s in total; 15 simultaneous with a 12 s
timeout gave 4 x 200 and 11 x 504, then the API recovered immediately); 200 KB and emoji messages work; capacity at
40-100 users was not measured.

## 5. Acceptance criteria (Issue #6, section 11)

| Criterion | Evidence |
|-----------|----------|
| `dotnet build` passes | section 2 |
| `dotnet test` passes | section 2 (105/105) |
| `/api/chat` calls real Ollama | section 3 |
| Valid message returns 200, contract, no nested JSON, model `qwen3:4b` | section 3, `OllamaChatApiTests` |
| Empty message returns 400 | section 3, integration tests |
| Unavailable 503 / timeout 504 / provider error 502 / invalid response 502 | section 4 and integration tests |
| Cancellation is forwarded | `Cancellation_ByCaller_StopsPromptly_...` |
| Automated tests need no Ollama | fake HTTP handler |
| Live smoke test passes with Ollama | section 3 |
| No secret in repository | secret scan of source and scripts found nothing |
| Ollama not exposed to the public internet | default `127.0.0.1`; the app never opens Ollama |
| README has reproduce steps | `README.md` |

## 6. Open items (not done by the developer)

- **Contract approval:** Issue #5 (AI-002) is still open and has no approval comment. The implementation follows its
  written contract; it still needs confirmation from the Backend Developer or Tech Lead.
- **Human code review** of the pull request and merge into `main` (owner only).
- **Post-merge check** of `/api/chat` (Definition of Done) after the owner merges.
- `main` (with BE-001 and the AI-001 Ollama setup docs) was merged into `watzapon01_Backend`; build and 105 tests pass on the merged result.
