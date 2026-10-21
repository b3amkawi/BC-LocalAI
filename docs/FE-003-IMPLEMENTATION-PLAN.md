# FE-003 — Real integration plan (2026-10-21, Asia/Bangkok)

User priority: pull main and finish FE-003; **do not push**. Main `d121f50` was merged locally in `c0a1d5e`.
Baseline after merge: .NET Release847 passed. Independent BE-007 review work remains at local `60ce52c`, not included here.

1. Preserve FE-002 and the explicitly offline Mock preview. Add real `/login` and a server-protected authenticated layout only
   when the existing Authentication flag is enabled. Do not enable authentication/history in checked-in configuration.
2. Use BE-005/006 cookie/CSRF/Me/Logout contracts; real transport has bounded bodies/deadlines, no redirects, no credential retry.
   Clear password immediately; keep all identity/history in memory only; abort and ignore stale requests on identity changes.
3. Use BE-007 APIs from main, not the independent WIP contract: CRUD sidebar, chronological cursor pages, message statuses,
   `conversationId` in `/api/chat/stream`, no automatic retries of mutations or AI. Explain actual main deletion semantics.
4. Reuse FE-002 parsing/rendering/key handling. Treat HTTP401/403 and SSE session-ended codes as identity boundaries.
   Check identity before mutations and after a long stream. Never render response strings as HTML.
5. Add unit, DOM and server integration tests; verify real HTTPS Login/MySQL/Ollama with synthetic accounts and browser evidence.
   Then run regression, isolated mutations, review, secret/doc/count gates and document actual results/limitations.

Recovery: changes stay on `watzapon_FE-003`; no production deployment, database migration/purge or main merge. Feature flags
remain off by default, and the prior anonymous Chat remains available in that configuration. Original worktrees are preserved.
Company credentials/schema have not been supplied; verification uses only approved synthetic fixtures, never employee credentials.

Explicit user approval (same chat): add narrowly scoped API integration guards and tests in FE-003, using the existing Cookie/Session.
History mutations require header CSRF; optional `X-BC-Expected-User` is compared to the authenticated server principal (never used
as an owner), and `X-BC-User` identifies the actor of each response so the UI rejects stale/cross-account responses.

Known dependency review: main and the independent BE-007 review differ in retention, context and server edge-case protections.
Do not silently import those backend changes into this UI issue or claim that browser controls fix a server-side defect.
