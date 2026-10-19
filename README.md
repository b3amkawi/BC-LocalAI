# BC-LocalAI

ASP.NET Core MVC web application targeting .NET 10.

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

## AI runtime (AI-001)

Follow [Ollama installation](docs/ai/ollama-setup.md) and [API guide](docs/ai/ollama-api.md).
PowerShell 7 (`pwsh`) is required for the verified workflow; check `pwsh --version` first.
Install it using the [official PowerShell Windows guide](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) if needed.
With Ollama running and `qwen3:4b` downloaded, run from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1
```

See [local test evidence](docs/ai/ollama-test-evidence.md) for results and hardware limitations.

To check the smoke script against a simulated local API without loading a model,
developers with Python 3 and PowerShell 7 can run:

```powershell
python -m unittest discover -s tests -v
```

Python is only needed for these developer tests, not for running Ollama or the smoke script.
