# BE-001 Reviewer Guide

Issue: #2 · PR: #3 · Branch: `watzapon01_Backend` · Target: `main` (**do not merge until review is done**)

Requirements: .NET 10 SDK. No Ollama and no secrets are needed.

## 1. Build and automated tests

```powershell
git fetch origin
git checkout watzapon01_Backend
dotnet build .\BC-LocalAI.slnx
dotnet test .\BC-LocalAI.slnx
```

Expected: 0 warnings / 0 errors, `Passed: 13, Failed: 0`.

## 2. Run and try the API

```powershell
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

In a second terminal either run the script or open `docs/api-examples.http`:

```powershell
.\scripts\smoke-test.ps1
```

Expected: six `PASS` lines and `All smoke tests passed`.

## 3. Acceptance criteria (Issue #2)

| # | Criterion | How to check | OK? |
|---|-----------|--------------|-----|
| 1 | `dotnet build` passes | step 1 | [ ] |
| 2 | `dotnet test` passes | step 1 | [ ] |
| 3 | Valid message returns HTTP 200 | smoke test case 1 | [ ] |
| 4 | Empty message returns HTTP 400 | smoke test cases 2-4 | [ ] |
| 5 | Response matches the API contract | compare with Issue #2 section 4 | [ ] |
| 6 | Controller calls AI through `IAIService` | `BC-LocalAI/Controllers/ChatController.cs` | [ ] |
| 7 | Demo works without Ollama | Ollama is not running during step 2 | [ ] |

## 4. Code review points

| Area | File | Look for |
|------|------|----------|
| Controller | `BC-LocalAI/Controllers/ChatController.cs` | validation, async, depends only on `IAIService` |
| Service | `BC-LocalAI/Services/IAIService.cs`, `MockAIService.cs` | interface boundary, fixed mock output |
| DI | `BC-LocalAI/Program.cs` | `IAIService` -> `MockAIService`; malformed-JSON 400 follows contract |
| Errors | `BC-LocalAI/Middleware/ExceptionHandlingMiddleware.cs` | JSON 500 `INTERNAL_ERROR`, no stack trace leaked |
| Tests | `BC-LocalAI.Tests/` | controller, service and HTTP integration tests |
| Secrets | whole diff | none hardcoded |

## 5. Known limits (by design, per Issue #2)

- `MockAIService` returns a fixed response; real Ollama is a separate issue.
- No authentication yet (separate issue).
- The API contract still needs Tech Lead confirmation.

## 6. Reviewer sign-off

- [ ] Tech Lead confirmed the API contract
- [ ] Code review approved on PR #3
- [ ] Merge into `main` (owner only)
