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

BE-005 (login, MySQL user store) is being written in parallel. BE-006 depends on nothing unmerged from it; the hand-over points are
`IUserSecurityStateSource` and `SessionService` (guide section 2).

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
| Role mapping | ⏳ role comes from `UserSecurityState.Role`; BE-005 maps it from the `users.role` column |
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
| Existing login/logout APIs not regressed | ⏳ they do not exist yet (BE-005) |

## 5. Limitations / remaining

- Sessions in memory: restart signs everyone out; single instance only (MySQL store later through `ISessionStore`).
- Data Protection keys use ASP.NET Core defaults; must be configured for multi-instance or machine moves.
- An answer already streaming when the account is disabled finishes (at most the 180 s Ollama deadline); the next request is refused.
- Not enabled in the real app until BE-005 (user store, login) and FE-003 (login page) exist and HTTPS is used.
- Login lockout / rate-limit numbers are BE-005.
