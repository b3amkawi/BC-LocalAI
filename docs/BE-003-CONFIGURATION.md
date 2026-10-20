# BE-003 D3 - Configuration and timeout policy

Issue: #16 · Settings live in `BC-LocalAI/appsettings.json` under `Ollama` and can be overridden with environment variables
(`Ollama__MaxOutputTokens=2048`). Every value is validated at startup: an invalid value stops the application with a clear message.

## 1. Settings

| Setting | Default | Allowed | What it controls |
|---------|--------:|---------|------------------|
| `Ollama:BaseUrl` | `http://127.0.0.1:11434` | loopback `http`/`https`, **`scheme://host:port` only: no user name / password, path (must be `/`), query or fragment** (checked on the parsed `Uri`) - such a value stops the application at startup | where Ollama is reached (AI-002: loopback only). Logs describe it only as `scheme://host:port` |
| `Ollama:Model` | `qwen3:4b` | non-empty | model name sent to Ollama and expected back |
| `Ollama:TimeoutSeconds` | `180` | 1 - 600 | **deadline for the whole exchange** with Ollama: connecting, waiting in Ollama's queue, generating and reading the answer |
| `Ollama:MaxOutputTokens` | `1024` | 16 - 4096 | output token limit sent to Ollama as `num_predict` (was a fixed `256` in the code) |
| `Ollama:MaxResponseBytes` | `1048576` (1 MiB) | 4096 - 16777216 | largest response body accepted from Ollama; protects memory |

## 2. Why these values (evidence in `docs/BE-003-ROOT-CAUSE.md`)

**`MaxOutputTokens = 1024`.**
- Measured answers with the AI-002 system prompt ("answer briefly") need 66 - 247 tokens, even when the user asks for a very long text. The old limit of 256 sat on that edge.
- 1024 is about 4 times the longest answer measured, so normal long answers finish (`done_reason = "stop"`).
- Cost: Ollama generates 73 - 77 tokens/s on the test machine, so a runaway answer that uses the whole limit takes about 14 - 17 s (256 tokens: about 3.5 - 4 s;
  4096 tokens: about 55 - 70 s; 73 - 77 tokens/s measured here, about 60 tokens/s in AI-004). Ollama serves one request at a time, so a higher limit lengthens the queue in the worst case.
- 4096 is the upper bound because the model context window (`num_ctx`) is 4096 by default; asking for more output than the context cannot be honoured.
- Raise it only after checking the queue effect (section 4); lower it to shorten the worst case.

**`MaxResponseBytes = 1 MiB`.**
- A real response is 2 - 4 KB. 1024 tokens of Thai is at most about 12 KB of UTF-8 (about 36 KB if the provider writes every character as `\uXXXX`).
  1 MiB leaves a very large margin while still bounding memory per request. The body is read in chunks and reading stops as soon as the limit is passed,
  so an endless or huge body never fills memory (tested with a stream that never ends).
- The limit is the limit of the **body**, not of the answer; the answer is bounded by `MaxOutputTokens`.

**`TimeoutSeconds = 180`** (unchanged).
- The AI-004 benchmark (see section 2a) did not give a reason to change the default. At the speed measured here one request takes 0.5 - 15 s, so 180 s covers a queue of about 12 worst-case
  answers or about 70 typical 3-sentence answers in front of a request.
- The value must not be unlimited and is capped at 600 s.
- **Reviewed against AI-004 (section 2a):** 180 s stays as the ceiling for one request. A queue limit is recommended by AI-004 but not implemented (needs approval).

## 2a. Review against the AI-004 benchmark (`docs/performance/benchmark-report.md`, branch `pao04_AI`)

AI-004 measured the **pre-BE-003** backend (`num_predict` fixed at 256) on the same kind of machine (RTX 4050 6 GB), 3,156 requests, concurrency 1/2/5/10. What it says about this issue:

| AI-004 finding | Effect on BE-003 |
|----------------|------------------|
| 105 of 105 long Thai prompts were cut at 256 tokens and answered `502 AI_INVALID_RESPONSE`; 12 English prompts needed 119 - 196 tokens and always finished | Confirms the root cause found independently in `BE-003-ROOT-CAUSE.md`; the fix (`MaxOutputTokens`, `AI_RESPONSE_TOO_LARGE`) addresses exactly this |
| Ollama serves one request at a time: throughput is constant at 1 or 10 concurrent (short ~4.3, medium ~0.7, long ~0.29 req/s); only the waiting time grows (medium P50 1.3 s -> 7.8 s) | Matches our measurement (10 concurrent short answers ~24 s in total). Sending requests to Ollama in parallel gains nothing |
| No timeout ever happened; the longest request took 28.7 s | No evidence for changing `TimeoutSeconds = 180`; it remains a ceiling, not a target |
| Generation speed ~60 tokens/s (AI-004) vs 73 - 77 measured earlier here | Use the slower figure: a runaway answer of 1024 tokens takes up to about **17 s**, not the 13 s written earlier, and lengthens the queue for others |
| Backend overhead over direct Ollama is small (medium/long within ~6 %); short is 11 - 20 % higher but AI-004 itself says that is within run-to-run noise | No optimisation of the backend layer is needed |

AI-004 recommendations for BE-003 and their status:

| # | AI-004 recommendation | Status in this branch |
|---|-----------------------|-----------------------|
| 1 | Handle answers cut at the token limit (raise `num_predict` or flag it) instead of a 502 invalid response | **Done**: `MaxOutputTokens` 1024 and `502 AI_RESPONSE_TOO_LARGE`. Needs the Tech Lead / AI-002 owner to approve the contract change. Not yet re-measured with the AI-004 scripts (they need PowerShell 7, not installed on this machine) |
| 2 | Limit concurrent requests to Ollama with a semaphore and a bounded queue; answer 429/503 at once when the queue is full | **Not done.** It adds a new error code and configuration, so it is a contract change that AI-004 says needs approval. See section 2b |
| 3 | Queue size from throughput (about 20 queued medium requests for a 30 s wait; an estimate) | Not done (depends on 2) |
| 4 | Pass the cancellation to Ollama so abandoned requests do not take queue time | **Done and tested** (service tests + real runs: after clients left during a long generation, the next request was answered in 0.3 - 0.5 s). Inferred from latency; Ollama's own log was not inspected |
| 5 | Keep 180 s as a per-request ceiling and give queue waiting its own timeout | Partly: 180 s kept; **queue wait and generation share one deadline** (cause and effect cannot be separated in a 504) |

## 2b. Open decision: a bounded queue in front of Ollama

Why it might be worth doing: with one request at a time, a burst of N users makes the last wait N x (answer time). With the 180 s ceiling, AI-004 estimates ~124 medium or ~52 long requests can queue before the last one gets a 504; a user will not wait that long. A semaphore (1 request at a time) plus a queue of, say, 20 and an immediate `503`/`429` when full would turn "wait up to 3 minutes then fail" into "told at once to try again".

Why it was not done here: it changes the API contract (new status and code, new settings, UI text), the issue does not list it as a deliverable, AI-004 states that it needs the Tech Lead's approval, and the queue size must come from a re-measurement on the target machine. Recommendation: approve it as a small follow-up issue (or as part of BE-004, which has to decide the same for streaming).

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
