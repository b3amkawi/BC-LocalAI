# BE-005 — Configuration, API and BE-006 / FE-003 Handoff

Updated 2026-10-21 · Refs [#21](https://github.com/b3amkawi/BC-LocalAI/issues/21)

## Scope / authority

Pulled main `8bffcc812cf268df874eacd9b97b022ea93a69bc` before continuing implementation (local merge `e4b7ad0`).
[SEC-001 PR #33](https://github.com/b3amkawi/BC-LocalAI/pull/33) replaced the original legacy-integration assumption:
BC-LocalAI owns its MySQL accounts; there is no company legacy verifier/source to integrate.
The reviewed schema is [database/sec001/schema.sql](../../database/sec001/schema.sql).
Use the [SEC-001 API contract](../security/authentication-api-contract.md). This replaces the earlier BE-005 preparation DTO draft.

**Disabled by default. This branch implements basic authentication, not production rollout, Chat authorization or account administration.**
Enable only for an isolated HTTPS validation environment until implementation/policy review and BE-006 protections are complete.
No company schema/password migration, Login UI, self-registration or admin HTTP endpoints are added.

## Components and dependency

- `MySqlAuthenticationRepository`: fixed, parameterized SELECT of required fields; `bc_auth_reader` only. No writes, transaction retry or admin credential.
- `MySqlAuthenticationBackend`: uppercase-invariant ASCII username lookup; exact password; verify before checking active/lockout/role; safe result only.
- `MySqlAuthenticationAttemptStore`: separate column-scoped writer, one conditional atomic UPDATE before authentication can succeed.
  Persists failed counts/lockout, resets on success; stale stamp/disabled/locked/deleted rows cannot be accepted.
- `IdentityPasswordVerifier`: framework IdentityV3, HMAC-SHA512 / 220,000 iterations / 16-byte salt / 32-byte subkey, matching SEC-001 fixtures.
  Unknown or unsupported/malformed hashes perform a dummy KDF and fail. Unbounded/other work factors are not silently accepted.
  No custom cryptographic password comparison, automatic rehash, password reset or duplicate hash store.
- Auth API / boundary: HTTPS, same-origin defense, antiforgery before mutation, bounded JSON, no-store, safe failures.
- Bounded server sessions plus ASP.NET Cookie Authentication/Data Protection; no custom JWT/token format.
- MySqlConnector **2.6.2**, MIT (package metadata inspected), net10.0 target; no ORM necessary for a single fixed SELECT.
  [Official release history](https://mysqlconnector.net/overview/version-history/),
  [TLS/timeouts](https://mysqlconnector.net/connection-options/),
  [Microsoft cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0),
  [Microsoft antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).

## Configuration (no secrets in source)

Set through environment/approved secret provider, never screenshots/CLI arguments/logs or committed JSON:

| Setting under `Authentication` | Requirement / default |
|---|---|
| `Enabled` | false; explicit opt-in only |
| `ReaderConnectionString` | secret provider; non-admin read-only DB account; database/host authorized by operator |
| `WriterConnectionString` | separate username AND secret on the same server/port/database; column grants below; UseAffectedRows=false |
| `KeyDirectory` | existing absolute private directory outside wwwroot; restrict filesystem ACL to service identity/SYSTEM before use |
| `AllowLocalDevelopmentDatabase` | false; true allowed only Development + literal loopback IP + encrypted TLS (Required or VerifyFull) |
| Connection-string TLS | VerifyFull in Production, approved CA + matching hostname; no trust-all callback/public-key-retrieval bypass |
| Connection-string bounds | ConnectionTimeout 1–15 seconds, MaximumPoolSize 1–32; recommend 3 seconds / 4 for local testing; PersistSecurityInfo/AllowLoadLocalInfile false |
| `CommandTimeoutSeconds` | 5 (allowed 1–15), cancellation propagated, no retry |
| `AccountAttemptsPerMinute` | confirmed 5, case-insensitive bucket across IPs |
| `IpAttemptsPerMinute` | confirmed 30; socket peer address, not untrusted forwarded headers |
| `GlobalAttemptsPerMinute` | confirmed 120 |
| `ConcurrentLogins` | confirmed 2 (max4); zero queue, before password verification |
| `MaxSessions` | proposed 1024 (max4096); full capacity fails closed503 |
| `LockoutThreshold` / `LockoutMinutes` | confirmed 5 consecutive failures /15 minutes; success clears count; expired lock starts a new sequence |

Rate limits are fixed 60-second windows, include successful/failed attempts, and use fixed HMAC-indexed arrays
(4096 account buckets / 1024 peer buckets), not attacker-grown dictionaries. Collisions may conservatively throttle unrelated callers.
Window boundaries can allow two adjacent bursts. Rate-budget counts reset on restart and are not shared across processes;
the **separate account lockout/counter is persisted in MySQL** and survives new application hosts/restarts.
Rate rejection is generic429 with Retry-After60; there is no username/DB record existence check before admission.
The user/watzapon explicitly confirmed 5 failures/15 minutes, success reset,5 account/30 IP/120 global attempts per minute and2 concurrent verifications in this task's chat.
This is the approved BC-LocalAI policy, not a claim about a nonexistent legacy system. Changes require another policy review.

The application rejects root/admin-named database logins as a guard, but this does not prove arbitrary reader grants:
DBA/operator must also verify SELECT-only permissions (local negative grant test is in the separate MySQL suite).
Only Windows DPAPI-protected keys are wired for enabled auth in this increment; another platform requires an approved key encryptor,
not plaintext keys. DPAPI is tied to the service OS identity; key backup/ACL/restore still require an operational review.
Do not copy an admin.cnf into application configuration. The .local fixture is development data only.

### Writer grants / persistence semantics

The reader remains SELECT-only. The writer must have only these permissions on the approved `users` table:

- SELECT: `id,is_active,security_stamp,access_failed_count,lockout_end_utc`
- UPDATE: `access_failed_count,lockout_end_utc,updated_at_utc`
- No hash/password access, no username/role/active/stamp UPDATE, no INSERT/DELETE/DDL/GRANT or account administration.

Provisioning code: `AttemptWriterBootstrap` in [FixtureFactory](../../tools/Sec001Fixtures/FixtureFactory.cs), opt-in isolated fixture only.
DBA/operator must provision equivalent column grants and distinct secrets for a real installation; the app never grants itself access/admin fallback.
Enabled installations from the earlier draft must provide the writer secret: missing/unsafe writer configuration fails startup, not silently disables persistence.

One parameterized UPDATE checks UUID + expected security stamp + active status + no active lock under the InnoDB row lock.
MySQL evaluates single-table SET assignments left-to-right: compute count from old expiry first, then expiry from the new count.
[MySQL UPDATE semantics](https://dev.mysql.com/doc/refman/8.0/en/update.html).
No app-memory read-modify-write counter or automatic retry of ambiguous/cancelled writes. Reader/writer unavailable =>503/no session.
Unknown/ineligible accounts execute a null-id/stamp, non-matching writer operation too: one bounded KDF and writer roundtrip for each valid-shaped attempt.

At failure5, DB UTC expiry becomes now+15 minutes. Locked attempts neither extend lock nor increment count.
After expiry, a failure starts count1; correct password resets count0/expiryNULL only if active/stamp/lock predicates still match.
Admin credential/status/role changes must rotate security_stamp as SEC-001 requires; stale verification is rejected, never retried.
`updated_at_utc` changes on attempts; BE-006 must not treat every updated timestamp as a password change.
Counters are consecutive since last success/expired lock, not a rolling observation window. Account-lockout DoS is a trade-off of the
user-approved policy; rate/concurrency limits bound work, not a guarantee against distributed abuse.
[OWASP authentication throttling/lockout guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html).

## API usage for FE-003

All calls same-origin HTTPS; use the fetch API same-origin credentials mode. No password/token in URLs or localStorage.

1. GET `/api/auth/csrf`:200 `{"token":"<request-token>"}` and Secure/HttpOnly/Strict antiforgery cookie.
2. POST `/api/auth/login`: JSON username/password and `X-CSRF-TOKEN`. Content-Type application/json, body <=16KiB including chunked transfer.
   Exact two lowercase fields required; duplicates/unknown fields/null/wrong types/malformed JSON rejected400.
   Username 1–128 ASCII letters/digits/`._-`; no trim; uppercase invariant only in repository lookup.
   Password 1–1024 UTF-16 code units, <=4096 UTF-8 bytes, well-formed Unicode; preserve spaces/newline/combining forms exactly.
3. Success200 envelope below. New server session and Secure/HttpOnly/Lax `__Host-BC.LocalAI` cookie, Path=/, no Domain.
   Re-login revokes the previous current session and creates a new random session key, not an alias for the old cookie.
4. GET `/api/auth/csrf` again after Login; old anonymous request token is invalid for the authenticated principal.
5. GET `/api/auth/me`:200 same user envelope, anonymous/expired/revoked401 `AUTH_REQUIRED` (no HTML redirect).
6. POST `/api/auth/logout`: valid fresh CSRF header,204; revoke server session and clear auth/CSRF cookies.
   Anonymous Logout is idempotent with a valid anonymous CSRF token. Fetch a fresh token after Logout.

```json
{"user":{"id":"<stable-uuid>","displayName":"Synthetic display","username":"synthetic.user","roles":["User"]}}
```

Roles come only from the verified DB record and are limited to User/Admin, never from request JSON.
Do not use username as history owner; BE-007 must use the stable UUID from the server principal.
Server session key/stamp/password/hash are never returned in this DTO. Credential DTO redaction is not protection against reflection-based logging:
never log/destructure requests, connection options or persistence projections; strings cannot be guaranteed zeroized in managed memory.

| HTTP | Code / behaviour |
|---|---|
| 400 | AUTH_INVALID_REQUEST, no input echo (including unsupported body media type) |
| 401 | AUTH_INVALID_CREDENTIALS: unknown/wrong/disabled/locked same body/status, one KDF per lookup result |
| 401 / 403 | AUTH_REQUIRED / AUTH_FORBIDDEN JSON, no HTML redirect |
| 403 | AUTH_CSRF_INVALID, or AUTH_HTTPS_REQUIRED before credential processing; HTTP is not redirected |
| 413 / 429 | REQUEST_TOO_LARGE / AUTH_RATE_LIMITED; before backend verification |
| 503 | AUTH_UNAVAILABLE on DB failure/timeout or exhausted session capacity; no mock fallback |
| 500 | INTERNAL_ERROR; auth boundary logs event5102 + trace ID only, not raw exception/SQL/password |

Every auth response is no-store, including rejects and disabled404. GET Login/Logout cannot mutate (404/405).
Origin/Fetch-Metadata rejects cross-site/same-site requests as defense-in-depth; absent browser headers still require CSRF for mutations.
No credentialed CORS is enabled. Inbound proxy headers are not trusted; reverse-proxy deployment needs a reviewed forwarding configuration.

## Basic session and BE-006 boundaries

Bounded in-process store has random per-login ID, 30-minute ticket/idle lifetime and 8-hour absolute ceiling.
Renewal occurs after half the idle lifetime and never exceeds absolute expiry. Logout and renewal races cannot recreate a removed entry.
Restart logs everyone out; multiple app processes are **not supported for this basic store/limiter**.
Cookie ticket is framework-protected; server validates its session key exists before Me and before renewal.

BE-006 must supply durable/shared sessions and fleet-wide rate budgets as needed, status/stamp/role revalidation, all-device revocation,
Chat/stream auth/CSRF enforcement, SSE revocation bounds, policy decisions and relevant tests.
**Existing `/api/chat` and `/api/chat/stream` are intentionally unchanged and remain anonymous in this increment**.
Do not expose the app to untrusted users merely because auth endpoints exist. Full account disable/reset effects on existing sessions
are not implemented here (only new Login checks account status). Admin provisioning scripts/audit are also BE-006 follow-up,
not an admin API in BE-005. BE-005 now supplies persisted attempts/lockout through a metadata-only writer; these are not deferred to BE-006.

## Run / verify / recovery

Use existing .NET10 SDK and HTTPS developer certificate, do not disable TLS/cookie security to use HTTP.
Set the Authentication settings privately, then run the existing HTTPS launch profile. Do not commit connection strings or fixture credentials.

For repeatable MySQL verification in this branch's worktree (new isolated fixture only):

```powershell
pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Initialize -Port 3319 -InstanceName be005-mysql
$env:BE005_MYSQL_PRIVATE_DIRECTORY = Join-Path (Get-Location) '.local/be005-mysql/private'
dotnet test tests/BE005.MySqlTests/BE005.MySqlTests.csproj -c Release
pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Stop -Port 3319 -InstanceName be005-mysql
```

Initialize only on a previously absent .local instance directory; it refuses overwrite. Start an existing fixture with `-Action Start -Port 3319 -InstanceName be005-mysql`.
Do not change a pre-existing instance to this port or reset it to make tests run.
The explicit MySQL project is not in the default solution/CI: it requires local authorized secrets and deliberately fails if unconfigured,
never silently skips. It checks fixed loopback3319/reader+writer identities, not arbitrary external databases. Default tests need no MySQL/Ollama.

Rollback: set Enabled=false and restart; auth endpoints404 and in-memory sessions gone. No DB rollback/data migration required:
no schema/hash/account migration was performed. Attempt metadata is intentionally retained, not reset on disable/restart.
Keep private keys/fixture data private; Stop preserves them. Production deployment, backup/restore and live company integration are **not performed** here.

Current evidence: [Attempts/lockout completion and review](BE-005-LOCKOUT-VERIFICATION.md).
Earlier implementation evidence: [Initial verification](BE-005-IMPLEMENTATION-VERIFICATION.md).
