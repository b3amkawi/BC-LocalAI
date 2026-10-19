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
