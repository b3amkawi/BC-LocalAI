# BE-004 Review Checklist

- [x] Branch was fast-forwarded from `origin/main` after AI-005 merged.
- [x] `POST /api/chat/stream` returns SSE and leaves `POST /api/chat` unchanged.
- [x] Ollama request uses `stream:true`, `think:false`, JSON schema and configured output limit.
- [x] UTF-8 and NDJSON are handled across arbitrary network reads.
- [x] Only decoded `answer` text is emitted as delta; thinking and JSON scaffolding are excluded.
- [x] A complete `done:true` + `done_reason:stop` stream ends with `completed`.
- [x] Incomplete, invalid, provider-error and length-limited streams never emit `completed`.
- [x] Client cancellation reaches the provider read.
- [x] `MaxResponseDrainSize = 0` prevents post-cancellation response draining.
- [x] Provider details, prompts and answers are not logged or returned in errors.
- [x] Per-request state was tested concurrently.
- [x] Unit, integration, Production, UI, Python contract and real-Ollama checks were run.
- [x] Mutation check proved model-mismatch tests fail when validation is removed.
- [x] Regression tests first reproduced 10 protocol/JSON/logging failures, then passed after the fixes.
- [x] A real loopback Kestrel provider proves early delivery, upstream cancellation/timeout and refusal to follow redirects.
- [x] Controller tests prove writable cancellation and no extra terminal event after downstream failure.
- [x] Raw UTF-8 is split one byte at a time, with JSON escapes and surrogate pairs also split across records.
- [x] Concurrency test synchronizes both in-flight calls before allowing either to finish.

This is an engineering self-review; independent Tech Lead approval is still pending. Findings and evidence are in
[BE-004-REVIEW-FIXES.md](BE-004-REVIEW-FIXES.md).

Review focus:

1. Confirm the AI-005 decision to map `done_reason:length` to terminal `error` with `AI_RESPONSE_TOO_LARGE` and `partial:true`.
2. Confirm a total 180-second timeout remains appropriate until separate first-record/idle settings are approved.
3. FE-002 must parse JSON `data`, concatenate deltas into one bubble and render text safely (not `innerHTML`).
