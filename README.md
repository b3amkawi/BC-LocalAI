# BC-LocalAI

ASP.NET Core application targeting .NET 10. It serves a Chat web page (FE-001) at `/` and the backend exposes `POST /api/chat`,
answered by a local Ollama model (`qwen3:4b`) through `OllamaAIService` (BE-002).
`MockAIService` (BE-001) is kept for automated tests, so `dotnet test` does not need Ollama.

> **Known limitations (read before merging / deploying):** an answer longer than the output limit (`Ollama:MaxOutputTokens`, default 1024 tokens) returns `502 AI_RESPONSE_TOO_LARGE`;
> Ollama answers one request at a time, so many simultaneous users queue and can receive `504 AI_TIMEOUT`; there is no
> authentication, rate limit or conversation history. The backend streaming endpoint exists, but the current FE-001 page still uses the
> non-streaming endpoint until FE-002. Details and measurements: [docs/KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md).

## Requirements

- .NET 10 SDK
- Ollama (on the same machine, loopback only) with the `qwen3:4b` model for running the API; not needed for `dotnet build` / `dotnet test`. Installation: [docs/ai/ollama-setup.md](docs/ai/ollama-setup.md)

## Run locally

```powershell
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

Open <http://localhost:5290> in a browser: this is the Chat page.

The page works in every environment, including `ASPNETCORE_ENVIRONMENT=Production` with `dotnet run`
(before this fix the CSS and JavaScript arrived empty there; see `docs/BE-003-VERIFICATION.md` section 5b).

A published copy (`dotnet publish -c Release -o <folder>`) must be started **from its own folder**, or with
`--contentRoot <folder>`: otherwise ASP.NET Core looks for `wwwroot` in the current directory and the page has no style or script.

```powershell
cd <folder>
dotnet BC-LocalAI.dll
```

## Chat web page (FE-001, streaming since FE-002)

Type a question and press **Enter** (or the **ส่ง** button) to send it; **Shift+Enter** starts a new line. Since FE-002 the answer
**appears while it is being written** (`POST /api/chat/stream`), in one bubble with the model name. While the AI is working the
**ส่ง** button becomes **หยุดสร้างคำตอบ** (Stop, also **Esc**): it stops the answer and the server stops generating; the text
received so far stays, marked "หยุดการสร้างคำตอบแล้ว". Only one answer runs at a time, so a question cannot be sent twice.
An answer that did not finish (stopped, failed or the connection dropped) is never shown as complete: it is marked
"คำตอบไม่สมบูรณ์". Behaviour and states: [docs/FE-002-STREAMING-UI.md](docs/FE-002-STREAMING-UI.md). The page works on desktop and on phones, and everything can be done with the keyboard
(Tab reaches the message area, the input and the button; the arrow keys scroll the message area).

![Desktop](docs/fe-001/desktop-conversation.jpg)

| Situation | Text shown to the user |
|-----------|------------------------|
| Empty message (not sent) / `INVALID_MESSAGE` | กรุณาพิมพ์ข้อความก่อนส่ง |
| `AI_UNAVAILABLE` | ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่ |
| `AI_TIMEOUT` | AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่ |
| `AI_PROVIDER_ERROR` | AI เกิดข้อผิดพลาด กรุณาลองใหม่ |
| `AI_INVALID_RESPONSE`, or a successful answer where `response` or `model` is missing, empty or not a string | ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่ |
| `AI_RESPONSE_TOO_LARGE` | คำตอบยาวเกินกว่าที่ระบบรองรับ กรุณาถามให้สั้นลงหรือแบ่งเป็นหลายคำถาม |
| `INTERNAL_ERROR` or anything unknown | ระบบเกิดข้อผิดพลาด กรุณาลองใหม่ |
| The server cannot be reached at all | ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ กรุณาตรวจการเชื่อมต่อแล้วลองใหม่ |
| The stream ended before the answer finished (FE-002) | การเชื่อมต่อขาดก่อนที่คำตอบจะเสร็จ คำตอบที่เห็นอาจไม่ครบ กรุณาลองใหม่ |

After an error the question is put back in the input so it can be sent again. Exceptions, stack traces and server messages are never shown.

Rules the page follows: it calls only `POST /api/chat/stream` (never Ollama on port 11434; `POST /api/chat` stays available for other clients); every text is written with `textContent`,
so HTML or script in an answer is shown as plain text; there is no CDN or new dependency; nothing is stored in the browser
(the conversation is kept in memory only and is gone when the page is reloaded). The page sends a Content-Security-Policy
header (`script-src 'self'`, `connect-src 'self'`, ...), which also stops the browser from calling anything but this site.

Files: `BC-LocalAI/Views/Home/Index.cshtml`, `BC-LocalAI/wwwroot/js/chat-core.js` (texts, validation), `chat-stream.js` (FE-002: SSE parsing and answer states), `chat.js` (page wiring), `css/chat.css`.
Colors come from the BetterCode logo (navy `#16386A`, yellow `#F9CB00`, red `#ED1C24`, white).
Review guide and evidence: [docs/FE-001-REVIEW-CHECKLIST.md](docs/FE-001-REVIEW-CHECKLIST.md), [docs/FE-001-evidence.md](docs/FE-001-evidence.md),
[docs/FE-002-REVIEW-CHECKLIST.md](docs/FE-002-REVIEW-CHECKLIST.md), [docs/FE-002-VERIFICATION.md](docs/FE-002-VERIFICATION.md).

### Tests for the Chat page

```powershell
dotnet test .\BC-LocalAI.slnx                    # includes the page, static-file and security checks
node --test "tests/ui/*.test.mjs"               # logic tests (Node 20+, no packages to install)
```

> **Stop the running app before `dotnet build` or `dotnet test`.** A running `BC-LocalAI.exe` locks the output files and the
> build fails with `MSB3021` / `MSB3027` ("Unable to copy file ... BC-LocalAI.exe"; typically `10 Warning(s), 2 Error(s)`).
> This is not a code problem: press Ctrl+C in the terminal running the app, then build again.

## Build

```powershell
dotnet build .\BC-LocalAI.slnx
```

## Test

```powershell
dotnet test .\BC-LocalAI.slnx
```

## Continuous integration (DEVOPS-001)

Every pull request to `main` runs `.github/workflows/ci.yml`: build, .NET, JavaScript and Python tests (with PowerShell 7), a test-count gate, a secret scan and a Markdown link check. No job needs Ollama or a GPU. The live suites (AI-003, AI-004, AI-005) are run on a developer machine and their evidence is pasted in the PR. See [CI quality gates](docs/devops/ci-quality-gates.md) and the [live test procedure](docs/devops/live-test-procedure.md).

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

HTTP 200 response (a real answer from Ollama; for example, asking "หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ"):

```json
{
  "response": "2",
  "model": "qwen3:4b"
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

There is no silent fallback to `MockAIService`: if Ollama is not reachable the API returns an error (see below).
No secrets are used. Ollama is reached through loopback only (AI-002 contract): `Ollama:BaseUrl` must be a loopback http/https URL (`127.0.0.1`, `localhost`, `[::1]`); any other host stops the application at startup, and so does a URL with a user name / password, path, query or fragment (logs only ever show `scheme://host:port`). Ollama itself must not be exposed to the public internet.

### Ollama configuration

`BC-LocalAI/appsettings.json` (override with environment variables such as `Ollama__BaseUrl`):

```json
{
  "Ollama": {
    "BaseUrl": "http://127.0.0.1:11434",
    "Model": "qwen3:4b",
    "TimeoutSeconds": 180,
    "MaxOutputTokens": 1024,
    "MaxResponseBytes": 1048576
  }
}
```

| Setting | Meaning | Allowed |
|---------|---------|---------|
| `TimeoutSeconds` | deadline for the whole exchange with Ollama (queue wait, generation, reading the answer); AI-002 suggests up to 300 on a machine without a GPU or for the first model load | 1 - 600 |
| `MaxOutputTokens` | output token limit sent to Ollama (`num_predict`); longer answers are cut by Ollama and reported as `AI_RESPONSE_TOO_LARGE` | 16 - 4096 |
| `MaxResponseBytes` | largest response body accepted from Ollama (protects memory) | 4096 - 16777216 |

Why these defaults, the timeout policy for every level (browser, Kestrel, service, Ollama, reverse proxy) and the log fields:
[docs/BE-003-CONFIGURATION.md](docs/BE-003-CONFIGURATION.md). Root cause of the old failures: [docs/BE-003-ROOT-CAUSE.md](docs/BE-003-ROOT-CAUSE.md).

The configuration is validated at startup; an invalid value (bad URL, a URL that is not loopback, empty model, a number outside its range) stops the app with a clear error.

### Error contract

All errors use `{ "error": { "code": "...", "message": "..." } }` and never include exceptions, stack traces or provider details.

| Case | HTTP | Code |
|------|------|------|
| Empty / missing message, malformed JSON | 400 | `INVALID_MESSAGE` |
| Cannot connect to Ollama | 503 | `AI_UNAVAILABLE` |
| Ollama timeout | 504 | `AI_TIMEOUT` |
| Ollama returns an HTTP error | 502 | `AI_PROVIDER_ERROR` |
| Malformed / incomplete Ollama response (`done` not true, `done_reason` other than `stop`/`length`, bad or empty `answer`, wrong model) | 502 | `AI_INVALID_RESPONSE` |
| Answer cut off by the output token limit (`done_reason` = `length`) or response body larger than `MaxResponseBytes` | 502 | `AI_RESPONSE_TOO_LARGE` |
| Unexpected error | 500 | `INTERNAL_ERROR` |

### Streaming Chat API (BE-004)

`POST /api/chat/stream` accepts the same JSON body and returns Server-Sent Events. A valid stream starts immediately with
`started`, sends one or more `delta` events containing decoded answer text, and ends with exactly one terminal event:
`completed` or `error`. The endpoint never forwards Ollama's JSON envelope, thinking field, provider error detail or stack traces.

```text
event: started
data: {"model":"qwen3:4b"}

event: delta
data: {"text":"2"}

event: completed
data: {"finishReason":"stop","evalCount":7}
```

The complete contract and error/cancellation behavior are documented in
[docs/BE-004-SSE-CONTRACT.md](docs/BE-004-SSE-CONTRACT.md).

Review corrections and repeatable live verification: [BE-004 review](docs/BE-004-REVIEW-FIXES.md),
[verification results](docs/BE-004-VERIFICATION.md). With the backend and Ollama running:

```powershell
python -I -X utf8 scripts/test-chat-stream.py --base-url http://127.0.0.1:5290 --output "$env:TEMP/be004-stream-results.json"
```

### Known limits

See [docs/KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) for the full list with measurements. In short:

- An answer that needs more than `Ollama:MaxOutputTokens` (default 1024) is cut off by Ollama and the API returns
  `502 AI_RESPONSE_TOO_LARGE` (before BE-003 the limit was a fixed 256 and the error was `AI_INVALID_RESPONSE`). The longest answers measured need
  about 250 tokens, so this is rare; a runaway answer that uses the whole limit takes about 13 s.
- Ollama handles one request at a time. Under a burst, requests queue and later ones can time out with `504 AI_TIMEOUT`
  (measured on the development laptop: 15 simultaneous 3-sentence answers with a 12 s timeout -> 4 succeeded, 11 got 504,
  and the API recovered immediately).
- No authentication, rate limit or conversation history. The Chat page uses `/api/chat/stream` since FE-002.

### Live smoke test (needs Ollama)

```powershell
.\scripts\live-smoke-test.ps1   # exit code 0 = passed, 1 = failed
```

Verification evidence: [`docs/BE-001-evidence.md`](docs/BE-001-evidence.md) (mock API),
[`docs/BE-002-evidence.md`](docs/BE-002-evidence.md) (Ollama integration).

## Review and testing files

| File | Purpose |
|------|---------|
| `docs/REVIEW-CHECKLIST.md` | BE-001 reviewer guide, acceptance criteria table, code review points |
| `docs/BE-001-evidence.md` | BE-001 build, test and API evidence |
| `docs/FE-001-REVIEW-CHECKLIST.md` | FE-001 (Chat page) reviewer guide and acceptance criteria table |
| `docs/FE-001-evidence.md` | FE-001 test results, browser test notes and screenshots (`docs/fe-001/`) |
| `docs/BE-003-ROOT-CAUSE.md`, `docs/BE-003-CONFIGURATION.md`, `docs/BE-003-VERIFICATION.md` | BE-003 root cause analysis, configuration and timeout policy, before/after verification report |
| `docs/BE-004-SSE-CONTRACT.md`, `docs/BE-004-CONFIGURATION.md`, `docs/BE-004-VERIFICATION.md` | BE-004 SSE contract, runtime behavior and verification evidence |
| `docs/KNOWN-LIMITATIONS.md` | Known limitations of the Ollama integration with measured numbers (read before merging) |
| `docs/BE-002-REVIEW-CHECKLIST.md` | BE-002 (Ollama) reviewer guide and acceptance criteria table |
| `docs/BE-002-evidence.md` | BE-002 build, test, live smoke test and failure-scenario evidence |
| `scripts/live-smoke-test.ps1` | Live check against a real Ollama (Thai, English, calculation, empty message) |
| `docs/api-examples.http` | Ready-to-send request examples (VS Code REST Client, Rider, Visual Studio) |
| `scripts/smoke-test.ps1` | Contract check of 200 and 400 cases against a running API (needs Ollama for the 200 case; works on Windows PowerShell 5.1 and 7+) |

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
Backend integration must follow the validated [Ollama integration contract](docs/ai/ollama-integration-contract.md).

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

## Chat quality evaluation (AI-003)

The [evaluation dataset](docs/ai/chat-evaluation-cases.json) contains 160 questions:
40 Thai, 40 English, 40 arithmetic, and 40 edge cases. Each case lists its complete
`accepted_answers`; after light normalization (NFKC, whitespace, trailing `.`/`!`, case),
the whole response must equal one of them, so a correct word inside a wrong sentence fails.
Start the Backend and run:

```powershell
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1
$LASTEXITCODE
```

The evaluator calls only the loopback Backend `POST /api/chat`; it does not query
Ollama directly. Exit 0 means every case passed. Any failed case returns exit 1
and remains in the JSON evidence with the actual Backend body and failure reasons.
See [evaluation results and reproduction steps](docs/ai/chat-evaluation-results.md)
for the measured cold/warm timings, environment, the latest 160/160 result, and limitations.

For a Thai explanation of AI-001 through AI-003, the tools used, file locations,
limitations, and questions for team handoff, read the [AI work handoff guide](docs/ai/ai-001-003-handoff.md).
For a beginner explanation of API/JSON terminology, the request flow, testing,
timing metrics, PowerShell syntax, and Git workflow, start with the [Thai learning guide](docs/ai/ai-001-003-explained.md).

## Ollama streaming contract (AI-005)

`scripts/test-ollama-streaming.ps1` (PowerShell 7, loopback only) tests the Ollama streaming API
(`stream:true`, NDJSON) with `qwen3:4b`: English/Thai, chunk assembly, Unicode, thinking behaviour,
truncation, HTTP errors, cancellation, timeouts and an interrupted stream, recording Expected vs Actual
in `docs/ai/ollama-streaming-evidence.json`. `scripts/OllamaStreaming.psm1` is the tested reference
reader. Read the [streaming contract](docs/ai/ollama-streaming-contract.md), the
[test results](docs/ai/ollama-streaming-test-results.md) and the
[Backend handoff for BE-004/FE-002](docs/ai/ollama-streaming-handoff.md). Tests that need no model:
`py -3 -m unittest tests.test_ollama_streaming -v`.

## Performance benchmark (AI-004)

`scripts/benchmark/ollama-benchmark.ps1` (Ollama `/api/generate`) and
`scripts/benchmark/backend-benchmark.ps1` (Backend `/api/chat`) send predefined short, medium and
long prompts (45 fixed prompts) at 1, 2, 5 and 10 concurrent requests and write per-request CSV rows
plus a JSON summary (latency percentiles, throughput, success/timeout rate, CPU/RAM/GPU/VRAM).
`scripts/benchmark/cold-start-benchmark.ps1` repeats the cold-start measurement. All need PowerShell 7
and are loopback only. See the [test plan](docs/performance/benchmark-plan.md),
the [performance report](docs/performance/benchmark-report.md) (preliminary results) and the
[handoff for BE-003/BE-004](docs/performance/benchmark-handoff.md).

## AI failure policy (AI-007)

`scripts/test-ollama-failures.ps1` (PowerShell 7, loopback only) injects ten kinds of failure into a **dedicated** Ollama instance it starts on port 11436 (runner killed, Ollama killed, Ollama not running, model unloaded, model not found, token limit, invalid request, client cancel, abandoned queued request) and records what the client sees and how long Ollama takes to recover. Your normal Ollama on port 11434 is never stopped. Read the [failure policy](docs/ai/ai-failure-policy.md) (retry rules, queue policy, user messages) and the [results](docs/ai/ai-failure-test-results.md). Tests that need no model: `py -3 -m unittest tests.test_ollama_failures -v`.
