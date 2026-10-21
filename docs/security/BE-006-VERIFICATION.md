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
  revoked, cookie/user mismatch, deleted / disabled / locked (kept since 2026-10-21, section 8), stamp change, role change up and down, unknown roles;
  options (defaults = decision, ranges, idle ≤ absolute, shipped appsettings off); store (256-bit unique ids, touch never moves CreatedAt or
  goes back, revoke final, revoke-all only that user, unknown role refused, 100 concurrent creates); same-site detection (13 cases).
- **Integration (24, real pipeline, auth on):** anonymous chat and stream → 401 JSON with no redirect and no SSE; page still loads;
  signed-in User chats; cookie flags (`__Host-`, Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, no Expires, no stamp or user id visible);
  User → admin-only 403 JSON, Admin → 200 and may chat; copied cookie useless after sign-out; revoke-all ends both devices of one user
  and not another user; disable / stamp / role / delete each end a **copied** cookie at the next request and it stays dead after
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
- Data Protection keys: BE-005's `Authentication:KeyDirectory` (DPAPI, survives restarts); multi-instance needs a shared approved key store.
- An answer in progress stops at the next piece after sign-out / revoke-all / 8 h, and within 5 s after an account change in MySQL (section 7).
- Not enabled in the real app until FE-003 (login page) exists and HTTPS is used.
- Login lockout / rate-limit numbers are BE-005.

## 6. Integration with BE-005 (2026-10-21)

| Suite | Result |
|---|---|
| `dotnet test` (all, after the merge) Development / Production | **641** passed, 0 warnings (baseline raised 535 → 641) |
| `dotnet test` with hostile environment (`Authentication__Enabled=true`, idle 999, absolute 1, `MaxSessions=1`) | **641** passed (`MaxSessions` now pinned in `TestSupport.cs` too) |
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

## 7. Review against AGENTS.md and decisions (2026-10-21)

Decisions taken by watzapon on Claude Code's recommendation (delegated by the project owner).

**Stopping an answer when the session ends** (review note on Issue #22: the stream must end with a defined error and send nothing more).
Implemented: `StreamSessionGuard` (section 2 of the guide). Maximum revocation delay: next piece for sign-out / revoke-all / 8 h;
5 s for account changes in MySQL (one query per 5 s per running answer). `/api/chat` does not hand over an answer that finished after the
session ended. Tests `MidStreamRevocationTests` (12): sign-out and revoke-all from another tab, disabled (not noticed before 5 s, noticed
at 5 s, session revoked and stays dead after restore), stamp / role / deleted (locked: section 8), 8 h reached mid-answer, user store down (stops, not
revoked, works again), idle timeout does not cut a 40-minute answer, no account query per piece, `/api/chat` answer withheld, auth off
unchanged. Mutation check (each made tests fail): guard always passes (10) · no account check (7) · not revoked (1) · revoked when the
store is down (1) · idle timeout cuts an active answer (1) · account queried for every piece (2) · `/api/chat` final check removed (1) ·
stream skips pieces silently instead of ending (9). Bug found by these tests and fixed: the 5 s interval first counted from the first
piece, so a slow first piece hid an account change; it now counts from the start of the request.

**Code review (AGENTS.md sections 6–12) of the production diff against `main`:** no blocking issue found. Fixed: stale comments that
still said BE-005 implements the user store. Added end-to-end tests for two fail-closed paths that were only unit-tested:
sessions full → login 503 `AUTH_UNAVAILABLE` while the signed-in user keeps their session; account database down at sign-in → 503, no
cookie (`LoginFailClosedTests`; the capacity mutation makes it fail). Checked: parameterized SQL only; no password, hash, stamp, session
id or cookie in logs or responses; 401/403/503 use the shared JSON error format; options validated at startup; no new dependency.

**Admin account script (create / disable / reset / revoke)** — not in BE-006. It needs a separate MySQL writer account, grants and
audit, which are their own high-risk change; Issue #22 lists it under the role model only. Until it exists, an administrator ends a
user's sessions by setting `is_active = 0` or rotating `security_stamp` in MySQL: refused at the next request, within 5 s for an answer
in progress (next request tested against the live MySQL fixture; mid-answer tested with the in-process user store). Recommended follow-up issue: "Admin account management script (writer account, audit, revoke)".

**Merge order** — BE-005 (#21) has no pull request and this branch already contains it (dc477f7) with its ticket store replaced. One
pull request from `watzapon_BE-006` to `main` (Refs #21, Refs #22) avoids reviewing BE-005's session code that no longer exists;
`watzapon_BE-005` should not be merged separately afterwards.

**Issue #22 text** — sections 4.7 to 7 (and the TC table that TC-16 refers to) are missing from the issue body (also noted in the issue's
comment). Verification is mapped to the acceptance criteria (section 4) and the objectives instead; the owner should restore the
missing sections so the TC list can be checked. BE-011 (Document ACL) named under dependencies has no issue yet.

**CI** — runs only for pull requests to `main`. The Python benchmark test that fails on this machine (section 6) also fails on a clean
`main` here, while the last CI runs on `main` (ubuntu) passed, so it is local to this machine.

## 8. After merging `main` 0cb07ff (BE-005 PR #34, lockout) — 2026-10-21

`main` received BE-005 through PR #34, including a new commit `aefd73e` (failed attempts and lockout persisted in MySQL through a
separate attempt writer, `WriterConnectionString`). Merged into `watzapon_BE-006` (no rebase, no force push) and checked again.

| Finding | Severity | Resolution |
|---|---|---|
| **Lockout signed the owner out.** BE-005 locks an account after 5 wrong passwords typed by anyone; BE-006 treated an active lockout as a reason to end every open session of that account (at the next request and within 5 s mid-answer). Anyone who knows a username could therefore sign its owner out every 15 minutes. | High (DoS on users) | Decision (Claude Code's recommendation, same as ASP.NET Core Identity): **lockout only blocks new sign-ins**; open sessions continue. Administrators end sessions by disabling the account or rotating `security_stamp`, which BE-005's documentation already requires for every admin change. `SessionValidator` no longer rejects on lockout (`SessionRejectReason.UserLocked` removed); `SessionService` still refuses to sign a locked account in. |
| Merge conflicts | — | `README.md` (kept main's lockout link and the BE-006 sentence), `docs/devops/test-baseline.json` (kept the higher count, raised to 652). |
| BE-006 integration tests with auth on failed at startup | Test only | Main now validates `WriterConnectionString` when auth is enabled; `SecurityHost` gets the same fake placeholder as BE-005's tests. |
| BE-006 live tests used the old fixture | Test only | Moved to main's dedicated BE-005 fixture (`-InstanceName be005-mysql`, loopback port 3319, reader + attempt writer + admin). |
| Python benchmark test failed locally | Fixed upstream | Main's `aefd73e` fixed the benchmark sample race: 143/143 now pass on this machine. |

Tests added or changed for the lockout decision: unit (locked user keeps a valid session), integration (`FailedPasswordLockout_KeepsTheSignedInSession_ButRefusesANewSignIn`,
`FailedPasswordLockoutDuringTheAnswer_DoesNotStopIt`), live MySQL (`FiveWrongPasswordsFromSomeoneElse_LockNewSignIns_ButDoNotSignTheOwnerOut`:
5 real wrong passwords from another app host lock the account in MySQL, a correct password from a third host is refused, the owner's
open session still answers 200; lock reset in `finally`). The "locked" cases were removed from the tests that expect a session to end.
Mutation: restoring "lockout ends the session" makes 3 unit/integration tests and the live test fail.

| Suite (after the merge) | Result |
|---|---|
| `dotnet test BC-LocalAI.slnx` Development / Production | **652** passed, 0 warnings (baseline 641 → 652) |
| `dotnet test` hostile environment (`Authentication__Enabled=true`, idle 999, absolute 1, `MaxSessions=1`) | **652** passed |
| `node --test "tests/ui/*.test.mjs"` | 61 passed |
| `python -m unittest discover -s tests` | **143** passed |
| `check_test_counts.py` (all three suites) | all gates passed |
| Live MySQL `tests/BE005.MySqlTests` (fixture `be005-mysql`, 127.0.0.1:3319) | **16** passed (BE-005 attempts/lockout + BE-006 account changes + lockout) |
| Secret scan / doc links / CI script tests | clean / OK / OK |

No remaining known defect. Unchanged limits: sessions in memory (single instance, restart signs out); not enabled until FE-003 and HTTPS;
admin account script is a follow-up; Issue #22 sections 4.7–7 still missing upstream.
