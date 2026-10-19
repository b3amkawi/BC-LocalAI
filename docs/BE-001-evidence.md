# BE-001 Verification Evidence

- Date: 2026-10-19 (Asia/Bangkok)
- Branch: `watzapon01_Backend`
- Scope: GitHub Issue #2 — Create ASP.NET Core Chat API with Mock AI
- Runtime: .NET 10

## 1–3. Background, Objective, and Scope

The ASP.NET Core application exposes `POST /api/chat` as the backend contract
between a future Web UI and AI provider. `IAIService` separates the controller
from the provider, while `MockAIService` makes the endpoint demonstrable without
Ollama.

Implemented scope:

- ASP.NET Core project
- `ChatController`
- `ChatRequest` and `ChatResponse`
- `IAIService`
- `MockAIService`
- input validation
- centralized exception handling
- unit and integration tests
- README run/build/test instructions

## 4. API Contract Evidence

Live smoke test against `http://localhost:5291`:

```text
POST /api/chat
Body: {"message":"สวัสดี AI"}
Status: 200
Response: {"response":"Mock AI response","model":"mock"}
```

```text
POST /api/chat
Body: {"message":""}
Status: 400
Response: {"error":{"code":"INVALID_MESSAGE","message":"Message is required"}}
```

## 5–6. Technical Guidance and Deliverables

- ASP.NET Core controllers and dependency injection are used.
- Controller and service responsibilities are separated through `IAIService`.
- API calls are asynchronous and accept `CancellationToken`.
- No secrets or external AI configuration are present.
- Ollama and Web UI are not required for this issue.
- Source code, automated tests, request/response examples, and README are included.

## 7. Acceptance Criteria Evidence

Build command:

```powershell
dotnet build .\BC-LocalAI.slnx -c Release
```

Observed result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

Test command:

```powershell
dotnet test .\BC-LocalAI.slnx -c Release --no-build --no-restore --verbosity minimal
```

Observed result:

```text
Passed! - Failed: 0, Passed: 13, Skipped: 0, Total: 13
```

The integration tests verify HTTP 200 and response contract for valid input,
and HTTP 400 with the required error contract for empty, whitespace, missing,
malformed JSON, and wrong-type payloads. Controller tests verify that the
controller calls AI through `IAIService`.

## 8–9. Dependencies and Out of Scope

- .NET 10 SDK and repository are available.
- The accepted API contract is implemented as written in Issue #2.
- Ollama integration, Web UI, production deployment, and authentication are not
  included in this branch.

## 10. Definition of Done and Code Review

Code review compared `origin/main...watzapon01_Backend` for security,
correctness, performance, maintainability, and test coverage.

- Critical findings: 0
- High findings: 0
- Secrets found: 0
- Diff whitespace errors: 0
- Build: passed
- Automated tests: 13/13 passed
- API smoke tests: passed

The remaining workflow gates are creation of the pull request, attachment of
this evidence to Issue #2, review of the pull request, and merge into `main`.
