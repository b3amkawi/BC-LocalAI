# BE-002 Reviewer Guide

Issue: #6 · Branch: `watzapon02_Backend` · Target: `main` (**do not merge until review is done**)

Requirements: .NET 10 SDK. Build and tests need no Ollama. The live steps need Ollama with `qwen3:4b`.

## 1. Build and automated tests (no Ollama needed)

```powershell
git fetch origin
git checkout watzapon02_Backend
dotnet build .\BC-LocalAI.slnx
dotnet test .\BC-LocalAI.slnx
```

Expected: 0 warnings / 0 errors, `Failed: 0` (132 tests).

## 2. Live check with real Ollama

```powershell
ollama pull qwen3:4b                                                        # once
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http   # terminal 1
.\scripts\live-smoke-test.ps1                                               # terminal 2
```

Expected: four `PASS` lines, `All live smoke tests passed`, exit code 0.
The example from Issue #6 should return `{"response":"2","model":"qwen3:4b"}`.

## 3. Try the failure paths by hand (optional)

Stop Ollama (or point the app at a closed port) and send a request:

```powershell
$env:Ollama__BaseUrl = "http://127.0.0.1:59999"
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

Expected: HTTP 503 `AI_UNAVAILABLE`. A non-loopback URL such as `$env:Ollama__BaseUrl = "http://192.168.1.10:11434"` stops the app at startup (loopback only). Other invalid settings (for example `$env:Ollama__TimeoutSeconds = "0"`) stop the app at startup.

## 4. Acceptance criteria (Issue #6)

| # | Criterion | How to check | OK? |
|---|-----------|--------------|-----|
| 1 | `dotnet build` passes | step 1 | [ ] |
| 2 | `dotnet test` passes | step 1 | [ ] |
| 3 | `/api/chat` calls real Ollama | step 2 | [ ] |
| 4 | Valid message returns 200, contract, model `qwen3:4b` | step 2 | [ ] |
| 5 | No nested JSON in `response` | step 2, `OllamaChatApiTests` | [ ] |
| 6 | Empty message returns 400 | step 2 | [ ] |
| 7 | Unavailable 503 / timeout 504 / provider error 502 / invalid response 502 | `OllamaChatApiTests`, step 3 | [ ] |
| 8 | Cancellation is forwarded | `OllamaAIServiceTests.Cancellation_ByCaller_...` | [ ] |
| 9 | Automated tests run without Ollama | step 1 with Ollama stopped | [ ] |
| 13 | Known limitations understood and accepted | `docs/KNOWN-LIMITATIONS.md` | [ ] |
| 10 | No secrets in the repository | review the diff | [ ] |
| 11 | Ollama reached through loopback only | `OllamaOptionsValidator` rejects non-loopback `Ollama:BaseUrl`; try step 3 with `http://192.168.1.10:11434` (app must not start) | [ ] |
| 12 | README has reproduce steps | `README.md` | [ ] |

## 5. Code review points

| Area | File | Look for |
|------|------|----------|
| Provider call | `BC-LocalAI/Services/OllamaAIService.cs` | request matches Issue #5, success conditions, timeout vs caller cancellation, no prompt/response logging |
| DTOs | `BC-LocalAI/Models/Ollama/` | separate from the public `ChatRequest` / `ChatResponse` |
| Configuration | `BC-LocalAI/Configuration/OllamaOptions.cs`, `appsettings.json`, `Program.cs` | validation at startup, loopback enforced (`IsLoopback`), no secrets |
| Error mapping | `BC-LocalAI/Services/AiServiceException.cs`, `Middleware/ExceptionHandlingMiddleware.cs` | codes/status match Issue #6 section 7, nothing internal leaks |
| Controller | `BC-LocalAI/Controllers/ChatController.cs` | still only calls `IAIService`, no Ollama logic |
| No Mock fallback | `Program.cs` | `IAIService` is Ollama only; tests override it explicitly |
| Tests | `BC-LocalAI.Tests/` | fake HTTP handler, no real Ollama, failure cases covered |

## 6. Known limits (full list with measurements: `docs/KNOWN-LIMITATIONS.md`)

- No streaming, conversation history, authentication, database, or multiple providers.
- Answers longer than 256 tokens are cut by Ollama and returned as `502 AI_INVALID_RESPONSE` (contract: `num_predict` 256).
- Ollama answers one request at a time: bursts of simultaneous users queue and can get `504 AI_TIMEOUT`; not measured at 40-100 users.
- No authentication or rate limit (separate issue).
- The Ollama contract (Issue #5 / PR #8) has not been approved in writing yet; confirm it. The implementation follows the AI-002 document in PR #8 (same request fields, success conditions and system prompt).

## 7. Reviewer sign-off

- [ ] Ollama contract (Issue #5) confirmed by Backend Developer / Tech Lead
- [ ] Known limitations (`docs/KNOWN-LIMITATIONS.md`) reviewed and accepted
- [ ] Code review approved
- [ ] Merge into `main` (owner only)
- [ ] `/api/chat` checked after the merge
