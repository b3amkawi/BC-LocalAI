# BC-LocalAI

ASP.NET Core application targeting .NET 10. The backend exposes a mock chat API
so the API contract can be developed and tested without running Ollama.

## Requirements

- .NET 10 SDK

## Run locally

```powershell
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

Open <http://localhost:5290> in a browser.

## Build

```powershell
dotnet build .\BC-LocalAI.slnx
```

## Test

```powershell
dotnet test .\BC-LocalAI.slnx
```

## Chat API

Start the application, then send a valid request:

```powershell
$body = @{ message = "สวัสดี AI" } | ConvertTo-Json
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5290/api/chat `
  -ContentType "application/json" `
  -Body $body
```

HTTP 200 response:

```json
{
  "response": "Mock AI response",
  "model": "mock"
}
```

An empty, missing or whitespace-only message (or a malformed JSON body) returns HTTP 400:

```powershell
$body = @{ message = "" } | ConvertTo-Json
Invoke-WebRequest `
  -SkipHttpErrorCheck `
  -Method Post `
  -Uri http://localhost:5290/api/chat `
  -ContentType "application/json" `
  -Body $body
```

```json
{
  "error": {
    "code": "INVALID_MESSAGE",
    "message": "Message is required"
  }
}
```

The current response is produced by `MockAIService`; Ollama is not required and
no secrets or external AI configuration are used.

Verification evidence for BE-001 is available in
[`docs/BE-001-evidence.md`](docs/BE-001-evidence.md).

## Review and testing files

| File | Purpose |
|------|---------|
| `docs/REVIEW-CHECKLIST.md` | Step-by-step guide for the reviewer, acceptance criteria table, code review points |
| `docs/BE-001-evidence.md` | Build, test and API evidence |
| `docs/api-examples.http` | Ready-to-send request examples (VS Code REST Client, Rider, Visual Studio) |
| `scripts/smoke-test.ps1` | Automated check of 200 and 400 cases against a running API (works on Windows PowerShell 5.1 and 7+) |

Quick check: start the API, then run `.\scripts\smoke-test.ps1`.

> The `Invoke-WebRequest -SkipHttpErrorCheck` example above needs PowerShell 7+.
> On Windows PowerShell 5.1 use the smoke-test script instead.

## AI runtime (AI-001)

Follow [Ollama installation](docs/ai/ollama-setup.md) and [API guide](docs/ai/ollama-api.md).
PowerShell 7 (`pwsh`) is required for the verified workflow; check `pwsh --version` first.
Install it using the [official PowerShell Windows guide](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) if needed.
With Ollama running and `qwen3:4b` downloaded, run from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1
```

For an interactive local chat that displays only the final structured answer:

```powershell
.\scripts\open-chat.cmd
```

The launcher finds PowerShell 7 from `PATH` or the bundled Codex runtime. Type `/bye` to exit.

See [local test evidence](docs/ai/ollama-test-evidence.md) for results and hardware limitations.

To check the smoke script against a simulated local API without loading a model,
developers with Python 3 and PowerShell 7 can run:

```powershell
python -m unittest discover -s tests -v
```

If the `python` command is unavailable on Windows but the Python Launcher is installed, use:

```powershell
py -3 -m unittest discover -s tests -v
```

Python is only needed for these developer tests, not for running Ollama or the smoke script.
