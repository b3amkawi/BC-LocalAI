# BE-003 D3 - Configuration and timeout policy

Issue: #16 · Settings live in `BC-LocalAI/appsettings.json` under `Ollama` and can be overridden with environment variables
(`Ollama__MaxOutputTokens=2048`). Every value is validated at startup: an invalid value stops the application with a clear message.

## 1. Settings

| Setting | Default | Allowed | What it controls |
|---------|--------:|---------|------------------|
| `Ollama:BaseUrl` | `http://127.0.0.1:11434` | loopback `http`/`https` only; **no user name / password (`@`), query (`?`) or fragment (`#`)** - such a value stops the application at startup | where Ollama is reached (AI-002: loopback only). Logs describe it only as `scheme://host:port` |
| `Ollama:Model` | `qwen3:4b` | non-empty | model name sent to Ollama and expected back |
| `Ollama:TimeoutSeconds` | `180` | 1 - 600 | **deadline for the whole exchange** with Ollama: connecting, waiting in Ollama's queue, generating and reading the answer |
| `Ollama:MaxOutputTokens` | `1024` | 16 - 4096 | output token limit sent to Ollama as `num_predict` (was a fixed `256` in the code) |
| `Ollama:MaxResponseBytes` | `1048576` (1 MiB) | 4096 - 16777216 | largest response body accepted from Ollama; protects memory |

## 2. Why these values (evidence in `docs/BE-003-ROOT-CAUSE.md`)

**`MaxOutputTokens = 1024`.**
- Measured answers with the AI-002 system prompt ("answer briefly") need 66 - 247 tokens, even when the user asks for a very long text. The old limit of 256 sat on that edge.
- 1024 is about 4 times the longest answer measured, so normal long answers finish (`done_reason = "stop"`).
- Cost: Ollama generates 73 - 77 tokens/s on the test machine, so a runaway answer that uses the whole limit takes about 14 s (256 tokens: about 3.5 s;
  4096 tokens: about 55 s). Ollama serves one request at a time, so a higher limit lengthens the queue in the worst case.
- 4096 is the upper bound because the model context window (`num_ctx`) is 4096 by default; asking for more output than the context cannot be honoured.
- Raise it only after checking the queue effect (section 4); lower it to shorten the worst case.

**`MaxResponseBytes = 1 MiB`.**
- A real response is 2 - 4 KB. 1024 tokens of Thai is at most about 12 KB of UTF-8 (about 36 KB if the provider writes every character as `\uXXXX`).
  1 MiB leaves a very large margin while still bounding memory per request. The body is read in chunks and reading stops as soon as the limit is passed,
  so an endless or huge body never fills memory (tested with a stream that never ends).
- The limit is the limit of the **body**, not of the answer; the answer is bounded by `MaxOutputTokens`.

**`TimeoutSeconds = 180`** (unchanged).
- No benchmark (AI-004) exists yet, so the default was not changed. At the measured speed one request takes 0.5 - 15 s, so 180 s covers a queue of about 12 worst-case
  answers or about 70 typical 3-sentence answers in front of a request.
- The value must not be unlimited and is capped at 600 s.
- **Provisional:** the final value and any concurrency limit must be reviewed when AI-004 results are available (the issue says so).

## 3. Timeout policy (what happens at each level)

| Level | Setting / behaviour | Result for the user |
|-------|--------------------|---------------------|
| Browser waits for `/api/chat` | the server renders `TimeoutSeconds + 20` s into the page (`data-timeout-seconds`); invalid or missing falls back to 200 s; never unlimited | the server's `AI_TIMEOUT` normally arrives first |
| ASP.NET Core / Kestrel | no execution limit of its own; `RequestAborted` is raised when the client disconnects | cancellation reaches the service |
| `OllamaAIService` | one deadline (`TimeoutSeconds`) linked to the caller's token | deadline -> `504 AI_TIMEOUT`; caller left -> no response, logged as information |
| `HttpClient` | `Timeout = Infinite` on purpose, because the service deadline already bounds every call (connect, headers, body) | - |
| Ollama | processes one request at a time; waiting in its queue counts against the deadline | `504 AI_TIMEOUT` when the queue is too long |
| Reverse proxy (IIS, nginx, ...) | none in this repository. **If one is added, its read/response timeout must be larger than `TimeoutSeconds`** (for example 200 s), otherwise it answers with its own 504 and the API's error contract is lost | - |

## 4. Error contract (additive)

| Case | HTTP | Code | Notes |
|------|-----:|------|-------|
| empty / invalid message | 400 | `INVALID_MESSAGE` | unchanged |
| Ollama not reachable (also a connection reset) | 503 | `AI_UNAVAILABLE` | unchanged |
| deadline elapsed | 504 | `AI_TIMEOUT` | unchanged |
| Ollama HTTP error | 502 | `AI_PROVIDER_ERROR` | unchanged |
| malformed / incomplete response | 502 | `AI_INVALID_RESPONSE` | now only for genuinely malformed bodies |
| **answer cut off by the token limit, or body over `MaxResponseBytes`** | **502** | **`AI_RESPONSE_TOO_LARGE`** | **new**; before, the token-limit case was `AI_INVALID_RESPONSE` |
| client cancelled | (no response) | - | stops the Ollama call; not an AI failure; logged at Information |
| unexpected | 500 | `INTERNAL_ERROR` | unchanged |

`AI_RESPONSE_TOO_LARGE` is the one visible change. It was requested by the issue ("Response Too Large -> an error that can be identified"). The HTTP status stays 502.
Clients that do not know the new code should treat it like any other 5xx; the Chat page shows its own text for it.

## 5. Logging

Each outcome writes one structured entry (event ids 4001 - 4007, `BC-LocalAI/Services/OllamaLog.cs`) with named fields such as `ElapsedMs`, `EvalTokens`,
`BodyBytes`, `MaxOutputTokens`, `Reason`, `StatusCode`, `TimeoutSeconds`. `Reason` is a short fixed category (for example `OutputTokenLimit`, `BodySizeLimit`, `AnswerNotJson`,
`ModelMismatch`). The question and the answer are never logged (tested with marker text in every scenario). Event ids are stable so alerts can rely on them.

| Id | Level | Meaning |
|---:|-------|---------|
| 4001 | Information | answered |
| 4002 | Warning | deadline elapsed |
| 4003 | Information | the caller cancelled; the Ollama call was stopped |
| 4004 | Warning | Ollama unreachable; field `Endpoint` = `scheme://host:port` only (never the configured URL as written); exception attached for diagnosis, server side only |
| 4005 | Warning | Ollama returned an HTTP error |
| 4006 | Warning | invalid response (with `Reason`) |
| 4007 | Warning | response too large (`OutputTokenLimit` or `BodySizeLimit`) |

## 6. Note on the AI-002 contract

`docs/ai/ollama-integration-contract.md` fixes `num_predict` at `256`. BE-003 makes it a setting with a default of `1024` because the fixed value is the root cause of this issue.
Everything else in the contract (fields, success conditions, loopback only, cancellation separate from timeout) is unchanged. The AI Engineer should update the contract document
to say "the backend sends `Ollama:MaxOutputTokens`" and re-run the contract tests with the new default. This repository's AI-002 files were not edited.
