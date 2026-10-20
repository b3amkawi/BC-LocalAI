# BE-004: Configuration and Resource Management

BE-004 reuses the validated `Ollama` settings introduced by BE-003:

| Setting | Default | Streaming behavior |
|---|---:|---|
| `BaseUrl` | `http://127.0.0.1:11434` | Loopback-only provider endpoint. |
| `Model` | `qwen3:4b` | Sent upstream and required on every normal NDJSON record. |
| `TimeoutSeconds` | `180` | One total deadline covering connection, queue, generation and stream reads. |
| `MaxOutputTokens` | `1024` | Sent as `options.num_predict`; `done_reason:length` becomes `AI_RESPONSE_TOO_LARGE`. |
| `MaxResponseBytes` | `1048576` | Cumulative UTF-8 NDJSON limit; overflow becomes `AI_RESPONSE_TOO_LARGE`. |

The streaming `HttpClient` uses `ResponseHeadersRead`, an infinite built-in client timeout (the service owns the deadline), and a
`SocketsHttpHandler` with `MaxResponseDrainSize = 0`, `AllowAutoRedirect = false`, and `UseProxy = false`. This preserves the loopback
boundary even when the provider returns a redirect or the machine configures a proxy. On client cancellation the linked token reaches `SendAsync`, stream reads and
SSE writes; disposing the response closes the upstream connection instead of draining it for the .NET default delay.

The service keeps decoder state inside each request. There is no shared answer buffer, preventing cross-request data leakage.

The byte guard applies before line buffering and reads at most `MaxResponseBytes + 1` bytes (one probe byte) from an endless response.
SSE buffering is disabled through `IHttpResponseBodyFeature`, and every event is flushed. Text from one Ollama fragment is batched
into one delta instead of flushing an event per character. This does not wait for the complete answer.

Streaming failures log event 4009 with bounded code/reason and elapsed time. Raw exception messages, provider bodies, prompts and answers
are excluded. Unexpected controller failures log the exception type only; the client receives the safe `INTERNAL_ERROR` message.

Known limitation: BE-004 implements a total deadline only. Separate first-record and idle timeouts proposed by AI-005 are not added
because they are not part of the existing validated BE-003 configuration and need a Tech Lead decision. Reverse-proxy buffering was not
tested; `X-Accel-Buffering: no` is sent for proxies that support it.
