# BE-002 Verification Evidence

- Date: 2026-10-19 (Asia/Bangkok)
- Branch: `watzapon02_Backend`
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
dotnet test  .\BC-LocalAI.slnx  ->  Passed! Failed: 0, Passed: 135, Skipped: 0, Total: 135
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

## 4d. Loopback-only enforcement (review comment on PR #9)

`OllamaOptionsValidator` now requires `Uri.IsLoopback` (AI-002: Ollama through loopback only).

| `Ollama:BaseUrl` | Result (real app) |
|------------------|-------------------|
| `http://127.0.0.1:11434` (default) | starts, `200 {"response":"2","model":"qwen3:4b"}` |
| `http://localhost:11434` | starts, `200` |
| `http://[::1]:11434` | starts (valid loopback), `503 AI_UNAVAILABLE` because Ollama here listens on IPv4 `127.0.0.1` only |
| `https://ollama.internal:11434`, `http://192.168.1.10:11434`, `https://ollama.example.com`, `http://0.0.0.0:11434` | application does not start: `Ollama:BaseUrl must be an absolute loopback http or https URL.` |

Tests: unit tests for accepted (`127.0.0.1`, `localhost`, `[::1]`, ...) and rejected hosts (including `0.0.0.0`,
`127.0.0.1.nip.io`, `localhost.example.com`), and integration tests that the application cannot start with a
non-loopback URL and does start with loopback URLs. Removing or weakening the rule makes 5 to 62 tests fail.

## 4e. Compliance with the AI-002 contract (files pulled from `pao02_AI`, commit `766800d`)

The AI-002 files (`docs/ai/ollama-integration-contract.md` and its evidence) were merged from `pao02_AI` into this branch.
Each "Backend must" requirement of the latest contract was checked against the code and tests:

| AI-002 requirement | Implementation | Verified by |
|--------------------|----------------|-------------|
| `BaseUrl` is parsed as an absolute `Uri`, checked with `Uri.IsLoopback`, and the application must not start otherwise | `OllamaOptionsValidator` + `ValidateOnStart` | unit tests (allowed: `127.0.0.1`, `localhost`, `[::1]`; refused: `192.168.1.10`, `ollama.internal`, `ollama.example.com`, ...), integration test that startup fails, real app refuses to start |
| Request fields, `system` prompt, `format` schema, `num_predict` 256, `stream`/`think` false | `OllamaGenerateRequest` | unit test of the request body, live smoke test |
| Nine success conditions (model, `done` boolean `true`, `done_reason` `stop`, one non-empty `answer`, no think markers, ...) | `OllamaAIService.ExtractAnswer` | unit tests for each failure, mutation checks |
| Parse both JSON layers with a JSON parser, never regex or string cutting | `System.Text.Json` for both layers | code review, unit tests |
| `done_reason=length` is truncated and must not be a success | `AI_INVALID_RESPONSE` (502) | unit + live (long essay) |
| No partial answer is returned as a success | every failure throws `AiServiceException` | unit + integration tests |
| Provider timeout is separate from user cancellation; a cancelled request must not answer 504 | service distinguishes by the caller's token; middleware logs a client cancellation and returns nothing | `OllamaCancellationApiTests` (HTTP level): a disconnect cancels the Ollama call and is not logged as a timeout; a real `HttpClient` timeout returns 504 `AI_TIMEOUT`; 4 deliberate bugs each fail these tests |
| Timeout is configuration and must not wait forever | `Ollama:TimeoutSeconds`, 1 to 600, default 180 | validator tests, live 504 scenario |
| Unreachable provider is a distinct class | 503 `AI_UNAVAILABLE` | unit, integration, live |

Real Ollama check of cancellation: a client that disconnects after 1 s during a long generation is handled quietly
(information log only, no timeout line, no error). Measured directly against Ollama and through the app, the next short
request is answered in 0.2 s after an abandoned request, so Ollama does not keep working on cancelled requests.
AI-002 suggests up to 300 s for machines without a GPU or on the first model load; the default here is 180 s
(`Ollama:TimeoutSeconds`, maximum 600).

## 5. Acceptance criteria (Issue #6, section 11)

| Criterion | Evidence |
|-----------|----------|
| `dotnet build` passes | section 2 |
| `dotnet test` passes | section 2 (135/135) |
| `/api/chat` calls real Ollama | section 3 |
| Valid message returns 200, contract, no nested JSON, model `qwen3:4b` | section 3, `OllamaChatApiTests` |
| Empty message returns 400 | section 3, integration tests |
| Unavailable 503 / timeout 504 / provider error 502 / invalid response 502 | section 4 and integration tests |
| Cancellation is forwarded | `Cancellation_ByCaller_StopsPromptly_...` |
| Automated tests need no Ollama | fake HTTP handler |
| Live smoke test passes with Ollama | section 3 |
| No secret in repository | secret scan of source and scripts found nothing |
| Ollama not exposed to the public internet | `Ollama:BaseUrl` must be loopback (validated at startup, the app refuses other hosts); Ollama itself listens on `127.0.0.1` only |
| README has reproduce steps | `README.md` |

## 6. Open items (not done by the developer)

- **Contract approval:** Issue #5 (AI-002) is still open and has no approval comment. The implementation follows its
  written contract; it still needs confirmation from the Backend Developer or Tech Lead.
- **Human code review** of the pull request and merge into `main` (owner only).
- **Post-merge check** of `/api/chat` (Definition of Done) after the owner merges.
- `main` (with BE-001 and the AI-001 Ollama setup docs) was merged into `watzapon02_Backend`; build and 135 tests pass on the merged result.
