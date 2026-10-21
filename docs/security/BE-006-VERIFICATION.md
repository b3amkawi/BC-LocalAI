# BE-006 — Security Verification Report (D6)

Issue: #22 · Branch: `watzapon_BE-006` (from `main` `8bffcc8`, which contains SEC-001) · Date: 2026-10-20
Decisions used (watzapon, delegated by the project owner): Authentication **off by default** until BE-005 + FE-003, sessions **in memory**,
account **checked on every request**; roles User/Admin, idle 30 min, absolute 8 h (SEC-001).
Measured on the test machine (RTX 4060 Laptop 8 GB); the reference spec is the RTX 4050 Laptop 6 GB.

## 1. What was built

See [Developer Guide](BE-006-DEVELOPER-GUIDE.md) and [Authorization Matrix](BE-006-AUTHORIZATION-MATRIX.md).
Changed outside `BC-LocalAI/Security/`: `Program.cs` (4 lines: a using, a comment, `AddBcLocalAiSecurity`, `UseBcLocalAiSecurity`), `appsettings.json` (section `Authentication`), the two chat controllers
(`[Authorize(Policy = ChatUser)]`), `TestSupport.cs` (pins the new settings), `docs/devops/test-baseline.json` (.NET minimum 416 → 511).
**No change for users while `Authentication:Enabled` is false**: all 437 existing tests pass unchanged and the real app answers as before.

**Update 2026-10-21 — BE-005 merged into this branch** (`origin/watzapon_BE-005` dc477f7, at the user's request). Integration:
BE-005's login/logout now go through `SessionService` (one cookie scheme `BC.LocalAI`); BE-005's `BasicAuthenticationTicketStore`
and its `OnValidatePrincipal` are removed (replaced by BE-006 sessions, which keep BE-005's `MaxSessions` cap); BE-006 adds
`MySqlUserSecurityStateSource` on BE-005's reader connection; the same-site guard now compares origins canonically (a BE-005 test
caught `https://localhost` vs `localhost:443`). See section 6.

## 2. Tests actually run

| Suite | Result |
|---|---|
| `dotnet test` Development / Production | **511** passed (437 before + 74 new), 0 warnings |
| `dotnet test` with hostile environment (`Authentication__Enabled=true`, `IdleTimeoutMinutes=999`, `AbsoluteLifetimeMinutes=1`) | **511** passed (settings pinned in tests) |
| `node --test "tests/ui/*.test.mjs"` | 61 passed |
| `python -m unittest discover -s tests` (PowerShell 7) | 141 passed |

New tests:

- **Unit (50):** every session rule including exact boundaries (idle 29/30/31 min, absolute 479/480/600 min while active),
  revoked, cookie/user mismatch, deleted / disabled / locked (and lock expiry), stamp change, role change up and down, unknown roles;
  options (defaults = decision, ranges, idle ≤ absolute, shipped appsettings off); store (256-bit unique ids, touch never moves CreatedAt or
  goes back, revoke final, revoke-all only that user, unknown role refused, 100 concurrent creates); same-site detection (13 cases).
- **Integration (24, real pipeline, auth on):** anonymous chat and stream → 401 JSON with no redirect and no SSE; page still loads;
  signed-in User chats; cookie flags (`__Host-`, Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, no Expires, no stamp or user id visible);
  User → admin-only 403 JSON, Admin → 200 and may chat; copied cookie useless after sign-out; revoke-all ends both devices of one user
  and not another user; disable / lock / stamp / role / delete each end a **copied** cookie at the next request and it stays dead after
  the account is restored; account read on every request; idle 30 min; active use still ends at 8 h; user store down → 401 (not 500) and
  works again after recovery; auth on without a user store → app refuses to start; cross-site POST (Fetch Metadata or Origin) → 403
  `AUTH_CSRF_INVALID` even with a valid session; same-origin POST allowed; disabled mode keeps chat open but admin-only still needs sign-in;
  audit log has events 5001/5003 and no stamp or cookie value.

### Mutation check

Each deliberate bug makes tests fail: revoked session accepted (1) · role change ignored (2) · idle timeout ignored (3) · no activity
update (1) · refused session not revoked (5, after strengthening the test) · user-store error lets the request in (1) · same-site guard off (2) ·
chat open with auth on (12) · cookie not HttpOnly (1) · no fail-closed startup check (1).

**Defect found in the tests by this check:** "refused session not revoked" first passed, because the browser client drops its cookie when a
request is refused, so the next request was anonymous anyway. The test now replays a copied cookie, which only server-side revocation stops.
Another found while testing: an `Authentication__Enabled` environment variable broke 155 unrelated tests; the shared test helper now pins
the settings.

## 3. Real application

| Check | Result |
|---|---|
| `Authentication__Enabled=true` without BE-005's user store | does not start: "Authentication:Enabled is true but no IUserSecurityStateSource is registered (provided by BE-005)" |
| Default (off), Production, real Ollama | `/api/chat` 200, `/api/chat/stream` 10 SSE events, page 200; no warnings |

## 4. Acceptance criteria (Issue #22)

| Criterion | Status |
|---|---|
| Secure cookie configuration | ✅ tested flags |
| Session expiration policy | ✅ idle 30 min / absolute 8 h, tested at the boundaries |
| Session revalidation | ✅ every request (account state, stamp, role) |
| Session revocation mechanism | ✅ sign-out, revoke-all, automatic on refusal; copied cookies useless |
| Disabled account handling | ✅ next request refused, session revoked |
| Approved role model | ✅ User/Admin (SEC-001 decision) |
| Role mapping | ✅ from the `users.role` column at sign-in and on every request (tested: backend saying Admin does not make a User an Admin) |
| Authorization policies | ✅ `ChatUser`, `Admin` |
| Chat APIs protected by policy | ✅ when enabled (tested); **off by default by decision** until login exists |
| Anonymous requests refused | ✅ when enabled |
| Correct 401 / 403 | ✅ JSON, no redirect |
| CSRF protection | ✅ same-site guard + SameSite=Lax + JSON-only; antiforgery tokens for `/api/auth/*` are BE-005 |
| Session hijacking measures | ✅ HttpOnly/Secure/`__Host-`, new session id at sign-in, server-side revocation, short lifetime |
| No password or session secrets in logs | ✅ tested (stamp, cookie value) |
| Security audit logging | ✅ events 5001–5007 |
| Unit / integration tests | ✅ 74 |
| Authorization matrix | ✅ D5 |
| Security verification report | ✅ this document |
| Existing login/logout APIs not regressed | ✅ all BE-005 API tests pass on BE-006 sessions; live MySQL suite passes |

## 5. Limitations / remaining

- Sessions in memory: restart signs everyone out; single instance only (MySQL store later through `ISessionStore`).
- Data Protection keys use ASP.NET Core defaults; must be configured for multi-instance or machine moves.
- An answer already streaming when the account is disabled finishes (at most the 180 s Ollama deadline); the next request is refused.
- Not enabled in the real app until FE-003 (login page) exists and HTTPS is used.
- Login lockout / rate-limit numbers are BE-005.

## 6. Integration with BE-005 (2026-10-21)

| Suite | Result |
|---|---|
| `dotnet test` (all, after the merge) Development / Production | **627** passed, 0 warnings (baseline raised 535 → 627) |
| `dotnet test` with hostile environment (`Authentication__Enabled=true`, idle 999, absolute 1, `MaxSessions=1`) | **627** passed (`MaxSessions` now pinned in `TestSupport.cs` too) |
| `node --test "tests/ui/*.test.mjs"` / CI secret scan / doc links / `check_test_counts.py` for .NET and Node | 61 passed / clean / OK / pass |
| `python -m unittest discover -s tests` | 140 of 141: `test_resource_samples_are_attached_to_scenarios` (benchmark script, no auth code) fails on this machine **and on a clean `main` 8bffcc8** — not caused by this branch, not fixed here |
| New: `LoginIntegrationTests` (real BE-005 login endpoints) | disabled after login → next `/me` 401; role from the account, not the backend; account disabled / missing / unknown role between password check and sign-in → same 401 `AUTH_INVALID_CREDENTIALS`, no cookie |
| New: `SessionServiceTests` | sign-in refused for missing / inactive / locked / unknown role / no stamp; profile claims cannot override id, role or session id |
| New: `OllamaCookieIsolationTests` (Issue §4.2) | signed-in user, real Ollama clients for `/api/chat` and `/api/chat/stream` with a recording stub: the request to Ollama has no `Cookie` / `Authorization` header and the cookie value, an unrelated browser cookie and a bearer token appear nowhere in its URL, headers or body |
| New: `SessionCapacityTests` | full store fails closed (`AuthenticationDependencyException` → 503); reclaims revoked, idle-expired and 8 h-expired sessions; keeps live ones |
| Live MySQL (`tests/BE005.MySqlTests`, isolated fixture 127.0.0.1:3318) | BE-005 suite + `Be006LiveSessionTests` (disable / stamp / role changed in MySQL → open session 401 at next request, stays dead after restore, fresh login works); runs serially because tests share fixture accounts |

Mutation check of the integration (each bug made tests fail): inactive account allowed to sign in (2) · reserved profile claims not
filtered (1) · dead sessions not reclaimed (3) · capacity not enforced (2) · login ignores a refused sign-in (3) ·
chat client forwards the browser `Cookie` to Ollama (1) · stream client forwards `Authorization` under another header name (1).

BE-005 files changed in this branch: `AuthenticationController.cs`, `AuthenticationRegistration.cs`, `BasicAuthenticationTicketStore.cs` (removed),
`AuthenticationApiTests.cs` / `AuthenticationSessionAndLimitTests.cs` (ticket-store tests removed; user store added to the factory).
