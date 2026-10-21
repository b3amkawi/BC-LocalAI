# BE-005 — Acceptance and Self-Review Checklist

2026-10-21 · [#21](https://github.com/b3amkawi/BC-LocalAI/issues/21) · Author self-review, not independent/human approval.

Scope source: SEC-001 selected new BC-LocalAI MySQL accounts; company legacy adapter/hash-format requirements are replaced,
not falsely marked as having tested a company system. The issue's example DTOs are superseded by the SEC-001 contract.

| Acceptance criterion | Implementation / evidence |
|---|---|
| SEC-001 review | Selected architecture/schema/API and sign-off-owner decisions in merged PR #33 and `docs/security/` |
| Read-only MySQL account | Reader-only SELECT; real DB UPDATE denied; metadata writes use the separately approved column-scoped writer |
| Parameterized queries | Fixed reader SELECT / attempt UPDATE; SQL-like input negative cases |
| Legacy adapter | Replaced by `MySqlAuthenticationBackend` per SEC-001 scope decision; separate from Chat |
| Reviewed password verification | Framework IdentityV3 and strict approved format/work-factor checks; exact Unicode/space vectors; no company legacy claim |
| Account status | Active/lockout/UUID/role/stamp checks plus atomic active/stamp/lock recheck |
| Login | POST `/api/auth/login`; validation, rate admission, DB/verifier/writer before session issuance |
| Logout | POST `/api/auth/logout`; current-session revocation, cookie clearing, CSRF |
| Current user | GET `/api/auth/me`; safe SEC-001 projection, anonymous401 |
| Approved authentication mechanism | ASP.NET Cookie Authentication / Data Protection, no custom JWT; bounded basic server sessions |
| Secure cookies | HTTPS, Secure/HttpOnly/Lax auth cookie, host-only/Path=/, idle30min/absolute8h tests |
| CSRF | Antiforgery before mutations; fresh-token tests around Login/Logout; cross-origin rejection |
| Rate limiting | Confirmed5/account,30/IP,120/global per minute and2 concurrent; persistent5 failures/15min lock |
| User enumeration | Same401 body/status for wrong/unknown/disabled/locked, one KDF and writer roundtrip; statistical timing not proven |
| No duplicate password/hash persistence | No secondary credential store, automatic rehash or hash migration; runtime writer cannot read/change hash |
| No credentials in source | Generated secrets in ignored private ACL-restricted fixture; final tracked-file scanner recorded in verification |
| No sensitive logs | Generic dependency failures and sanitized event fields; response/log canary regression tests |
| Unit tests | Authentication business/config/verifier/session/limiter/security tests; focused109 and full546 |
| Integration tests | Test-host API/CSRF/session flows and explicit real MySQL12-case suite |
| Security report | [Current verification](BE-005-LOCKOUT-VERIFICATION.md); earlier evidence clearly marked historical |
| Chat regression | Full .NET/Node tests; Chat/Ollama/UI source unchanged. Live Qwen E2E is not claimed |
| Review per AGENTS.md | Author self-review below; independent reviewer/owner acceptance remains a normal merge gate |

## Author self-review

- Correctness/data integrity: atomic single-table conditional UPDATE, DB UTC, no retry after ambiguous writes,
  real concurrent/expiry/reset tests and restored mutation checks; no read-modify-write race in app memory.
- Security: SELECT-only reader, separate column writer, distinct identity/secret, TLS validation, no admin fallback,
  unchanged CSRF/cookie checks, no hash/role/status mutations, no auth session after stale/failed write.
- Reliability/performance: bounded pools/command timeout/cancellation, at most one SELECT + KDF + writer operation;
  no unbounded attempt dictionary or new dependencies in this completion. Connection/grant availability is required.
- Compatibility: authentication remains opt-in; schema/Chat/UI/AI-002 contract unchanged. Existing enabled draft installs
  must configure writer before startup. BE-006's separate session replacement must be preserved on later integration.
- Test quality: assertions cover boundary5, expired lock, success reset, disabled/stale/deleted, unknown, parallel20,
  cancellation/timeout and denied DB privileges. Intentional defects fail assertions in an isolated copy, then pass after restoration.
- Remaining limitations: basic single-process session/rate budget, no live model verification, no production rollout,
  no proof against all attacks and no independently approved code review. Exact execution status is in the current report.

Recommendation: review the completed local diff and evidence, then integrate with BE-006 deliberately and rerun its combined
session tests before merging that combination. No GitHub approval, Issue closure or merge is asserted/performed here.
