# BE-003 D1 - Root Cause Analysis

Issue: #16 - Fix Response Length & Timeout Handling · Date: 2026-10-20 · Machine: Windows 11, RTX 4050 Laptop (6 GB), Ollama + `qwen3:4b`

**สรุปสั้น (ไทย):** `AI_INVALID_RESPONSE` เมื่อคำตอบยาวไม่ได้เกิดจาก parser, ขนาด body, Unicode หรือ timeout แต่เกิดจาก
**ขีดจำกัด output token `num_predict = 256` ที่ฝังตายตัวในโค้ด** ซึ่งอยู่ติดขอบความยาวคำตอบจริง (66-247 token) เมื่อ Ollama ถูกตัดที่ 256
(`done_reason = "length"`) JSON ที่ได้ไม่สมบูรณ์ และ Backend จัดเป็น "response ผิดรูปแบบ" รวมกับ response เสียจริง จึงแยกไม่ออกทั้งใน error code
และใน log ส่วน `AI_TIMEOUT` ไม่ใช่บั๊ก แต่เป็นผลของคิวใน Ollama (ประมวลผลทีละคำขอ) ที่ชนเพดานเวลา โดยที่เพดานเวลาเดิมครอบด้วยกลไกเดียวและฝั่งหน้าเว็บตั้งค่าตายตัวไม่สัมพันธ์กับเซิร์ฟเวอร์

## 1. Symptom (reproduced before the fix)

`POST /api/chat` with a question that makes the model write a lot, on the code of `main` at `a484f2e`:

```text
history run 1  502  4.5 s  {"error":{"code":"AI_INVALID_RESPONSE", ...}}
history run 2  502  3.4 s  ...  (runs 3-6: 502, 3.3 s each)      -> 6 of 6 failed
app log (6 times): "Ollama response did not meet the success conditions."      <- no reason given
```

The question was: *"ตอบให้ยาวที่สุดเท่าที่จะทำได้ อธิบายประวัติศาสตร์กรุงเทพมหานครทุกยุคทุกสมัย ... อย่างน้อย 20 หัวข้อ"*.
The request took 3.3 s, far below the 180 s timeout, so it is not a timeout problem.

## 2. Evidence from Ollama itself (the application is not involved)

The exact request the backend sent (`format` schema, `think:false`, `temperature:0`, system prompt of AI-002) was sent straight to Ollama.

| Prompt | `num_predict` | `done_reason` | `eval_count` (tokens) | JSON inside `response` | Body size |
|--------|--------------:|---------------|----------------------:|------------------------|----------:|
| history (long) | 4096 | `stop` | **247** | complete, one `answer` | 2.9 KB |
| history (long) | **256** | **`length`** | **256** | **truncated, no closing `"}`** | 3.4 KB |
| list10 | 256 | `length` once (first run), `stop` (157 tokens) in the next 5 runs | 256 / 157 | truncated once, valid otherwise | 3.6 KB / 2.5 KB |
| essay 1500 words | 4096 | `stop` | 192 | valid | 2.9 KB |
| tutorial (English) | 4096 | `stop` | 66 | valid | - |
| poem 30 stanzas | 4096 | `stop` | 101 | valid | - |

What this shows:

1. **The cut-off is the output token limit.** When Ollama reaches `num_predict` it stops (`done_reason = "length"`, `eval_count == num_predict`)
   and the structured JSON is left unfinished. The same prompt with a higher limit finishes normally (`stop`).
2. **It is not the parser or the body size.** The failing bodies are 2.5-3.6 KB; the successful ones are the same size. Nothing near any size limit.
3. **Not Thai/Unicode.** Complete Thai answers parse correctly; only truncated ones fail.
4. **256 sits on the edge of the real answer lengths.** Because the system prompt asks for a brief answer, the longest answers measured need 190-250 tokens.
   The `history` answer needs 247 tokens: at a limit of 256 it either fits or is cut off depending on the run. Output was not identical between
   runs even at `temperature: 0` (list10: 157 tokens in five runs, 256 tokens in the first one), so the failure is intermittent for questions near the boundary.
5. Generation speed is 73-77 tokens/s after the model is loaded (the first call after idle also loads the model: 6.5-8.6 s), so
   1024 tokens need about 14 s and 4096 tokens about 55 s.

## 3. Evidence from the code (before the fix)

| File | What it did | Problem |
|------|-------------|---------|
| `Models/Ollama/OllamaGenerateRequest.cs` | `NumPredict { get; init; } = 256;` | fixed in code, not configuration |
| `Services/OllamaAIService.cs` `ExtractAnswer` | one condition: `body.DoneReason != "stop"` -> `AiServiceException.InvalidResponse()` | `length` (answer too long) and a broken body give the same code and the same log line |
| `Services/OllamaAIService.cs` | `PostAsJsonAsync` + `ReadFromJsonAsync` | the whole body is buffered with no size limit |
| `Program.cs` | `client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds)` | one timeout for everything, enforced inside HttpClient |
| `wwwroot/js/chat.js` | `REQUEST_TIMEOUT_MS = 200000` | fixed number, not tied to the server setting |
| logging | `LogWarning("Ollama response did not meet the success conditions.")` | no reason, no numbers: an operator cannot tell *why* |

## 4. What it is NOT (checked)

- Not the JSON parser: a 20,000-character Thai answer in both raw UTF-8 and `\uXXXX` form parses exactly (tests `LongThaiAnswer_*`, `Unicode_*`).
- Not a body size limit: there was none; the failing bodies are small.
- Not a timeout: failures came back in 3.3-4.5 s.
- Not the Web UI: `curl` against the API shows the same 502.

## 5. Timeout analysis, level by level

| Level | Finding | After BE-003 |
|-------|---------|--------------|
| ASP.NET Core / Kestrel request | no execution time limit by default; `RequestAborted` fires on disconnect (verified: the Ollama call is cancelled, the next request is answered in 0.2 s) | unchanged; the service owns the deadline |
| `HttpClient.Timeout` | was the only timeout (180 s) and covered the whole exchange | infinite on purpose; the real deadline is in `OllamaAIService` |
| Ollama processing | one request at a time: 10 simultaneous 3-sentence answers took 26 s in total (last one waited 26 s); with a 12 s limit 15 simultaneous requests gave 4 x 200 and 11 x 504; recovery was immediate | same behaviour, now with a documented budget |
| Reverse proxy | none in this repository | documented requirement: proxy read timeout must be larger than `TimeoutSeconds` |
| Browser | fixed 200 s | server renders `TimeoutSeconds + 20` into the page |
| Client cancellation | already forwarded; separate from timeout (BE-002 / AI-002) | also covers reading the body; no success is ever returned to a caller that has left; an already cancelled caller never reaches Ollama |

## 6. Concurrency

No concurrency limit exists and none is added. When this analysis was written AI-004 was not available; its results are now on `pao04_AI`
(`docs/performance/benchmark-report.md`) and were compared with this work in `docs/BE-003-CONFIGURATION.md` sections 2a and 2b: they confirm the root cause (105/105 long Thai
prompts cut at 256 tokens), confirm that Ollama answers one request at a time, and recommend a bounded queue, which is **not implemented** and waits for approval.
