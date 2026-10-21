# BE-005 — Implementation Verification / Security Review Record

2026-10-20 · branch `watzapon_BE-005` · Refs [#21](https://github.com/b3amkawi/BC-LocalAI/issues/21)

> Historical snapshot of the initial implementation/results, not current guidance. The 2026-10-21 completion
> adds the previously missing persisted writer/lockout and confirms policy numbers. See
> [current verification and remaining boundaries](BE-005-LOCKOUT-VERIFICATION.md) and [current configuration](BE-005-INTEGRATION.md).
> Statements below about no writer, deferred lockout, port3318 and535 tests apply only to this earlier snapshot.

## Scope and source

User requested pull main, continue BE-005, test/fix repeatedly, then push only this issue branch.
Pulled `main` at `8bffcc812cf268df874eacd9b97b022ea93a69bc`; merge `e4b7ad0` preserves preparation commit `c259a4c`.
New main adds SEC-001 PR #33, its selected local MySQL account architecture/schema/framework hasher and tests.
Original Legacy adapter/hash ACs are N/A/replaced according to the decisions in [SEC-001 handoff](../security/legacy-auth-review.md).
No invented company schema or algorithm. Implementation/policy still needs its own review, not inferred from SEC-001 merge.

## Changed components

Configuration validation + MySqlConnector, read-only parameterized repository, reviewed framework verifier/backend,
Auth controller and boundary, ASP.NET Cookie Authentication with bounded revocable basic sessions, antiforgery and bounded attempt limiter.
Updated DTO shape to SEC-001 envelope (earlier draft replaced), tests, .NET test-count minimum467→535, and integration/runbook documentation.
Only explicit `Authentication.Enabled=true` enables routes; checked-in default remains false.
No Chat controller/service/UI/AI contract modification, no Company DB/password migration, no account/hash duplication or app DB writer.

## Actual executed checks

| Check | Actual outcome |
|---|---|
| Release build main tests project, --no-restore | 0 warnings, 0 errors |
| Focused authentication suite | **98 passed /0 failed /0 skipped** |
| Full .NET regression: Development (final) | **535 passed /0 failed /0 skipped**, 23 seconds |
| Full .NET regression: Production (final) | **535 passed /0 failed /0 skipped**, 23 seconds |
| Node tests `node --test 'tests/ui/*.test.mjs'` | **61 passed /0 failed /0 skipped** |
| Python `python -I -X utf8 -m unittest discover -s tests -p 'test_*.py'` (final rerun) | **141 tests, OK /exit0**, 169.936 seconds |
| Separate real MySQL suite `dotnet test tests/BE005.MySqlTests/BE005.MySqlTests.csproj -c Release` | **4 passed /0 failed /0 skipped**, repeated after fixes |
| `dotnet list BC-LocalAI/BC-LocalAI.csproj package --vulnerable --include-transitive` | no vulnerable packages reported by configured current sources; not proof of no unknown vulnerabilities |
| Mutation after valid copy setup | **3 failed /8 passed /exit1**; assertions detected all 3 injected defects |
| Mutation copy restored/rebuilt/rerun | **11 passed /0 failed /exit0** |
| Final staged secret scan | **317 files checked, clean**; tracked/staged files, not Git history |
| Markdown link check | **60 Markdown files checked, OK**; local links, not external URLs/anchors |
| `git diff --cached --check` | Passed |
| Local MySQL fixture Verify after all auth tests | **11/11 passed**; Stop succeeded, data retained, existing3306/3307 untouched |
| Test-count gate against raw final logs | .NET535 >=535, Node61 >=23, Python141 >=120; all0 failures/skips, passed |
| Private instance ACL / key protection metadata | ACL protected,2 rules,0 inherited;1 key XML file, encryptedSecret present; no secret/key contents disclosed |
| Chat controllers/services/UI/CI workflow diff vs pulled main | Unchanged |

Raw build/regression logs are local ignored `artifacts/be005-verification/` (not published paths/secrets).
No claim of GitHub Actions/PR review passing. Test-count gate uses actual raw logs, not a manufactured test summary.
Python reports existing CSV/socket ResourceWarnings in benchmark/test tooling; exit0, not warning-free.
These files are unrelated to authentication and were not changed to hide warnings or loosen tests.

## Verification coverage

| Area | Evidence / expected behaviour confirmed |
|---|---|
| Repository | Real reader SELECT succeeds; UPDATE no-op denied1142; SQL-like lookup returns no account; only selected fields mapped |
| Cancellation/timeout | Already-cancelled and mid-query cancellation propagate; dedicated fixture table lock causes safe command-timeout failure; unreachable local DB becomes safe dependency error |
| Credential/status | Real active correct login succeeds; wrong/unknown/disabled/locked all401 with identical body; no auth-cookie issuance on failure |
| Hash integrity | Stored hashes unchanged before/after flow; actual framework exact Thai/emoji/space/newline password vectors and wrong-password rejection |
| Input | Duplicate/unknown/malformed/wrong-type/null body rejected400; invalid username rejected; unknown-length/chunked body >16KiB413 |
| API | Real MySQL→backend→Login200→Me200 same user projection→Logout204→Me401; Development/Production test-host flow too |
| Cookie/session | HTTPS only, Secure/HttpOnly/Lax/Path=/, no Domain; no custom token, re-login rotates server key; old cookie401 after re-login/Logout |
| Session boundaries | Fake-clock idle30 minutes/absolute8 hours; renewal cannot revive revoked/expired entry; capacity fails503 and expired slots reclaimed |
| CSRF/origin | Missing token403 including with valid auth cookie; old anonymous token invalid after Login; fresh authenticated token works for Logout; cross-origin rejected |
| Rate/CPU | Sixth account attempt429 before backend; case-insensitive account budget, IP/global budget, bounded zero-queue concurrency with idempotent release |
| Enumeration | Generic status/body across negative account states; service invokes one KDF even for missing/disabled/locked results; no rigorous statistical timing analysis claimed |
| Errors/logs | Auth failure raw canary password/hash/SQL never appears in captured body/log message/fields/exception; no mock fallback on DB failure |
| Configuration | root/admin rejected; production requires VerifyFull; loopback TLS exception Development-only; unsafe pool/limits/key path rejected with sanitized message |
| Disabled default | Auth returns404/no cookies even if a test success backend is registered; no automatic activation when code is merged |

The live suite uses only a new instance in this worktree's ignored ACL-restricted `.local/sec001-mysql-v2`, port3318,
generated accounts only. MySQL80/3306 and existing3307 were not modified.
Test-only admin connection temporarily locks the fixture users table and releases it in finally; there are no row updates.
Runtime uses the reader only. No credentials, real fixture passwords/hashes, private logs or keys are included in evidence.

## Problems found and corrected (not just final green output)

1. Name collision with ASP.NET `AuthenticationOptions`, then missing host using / await-span test compilation:
   corrected aliases/import/test expression; final build clean, no suppressed compiler warnings.
2. CSRF boundary originally ran before authentication. Valid authenticated Logout was403 in both environments.
   Moved CSRF validation after UseAuthentication, while HTTP auth remains rejected rather than redirected. Both flows pass.
3. Real MySQL lookup returned safe503 because CHAR(36) UUID mapping used GetString.
   Switched to typed GetGuid→canonical D string; actual SELECT and HTTP flow now pass. Retained real DB regression coverage.
4. Re-login with framework ITicketStore key reuse after SignOut produced an invalid new cookie.
   Replaced that wiring with a per-login random key claim in the protected framework ticket + explicit bounded server validation/revocation.
   Re-login gets a new key, old-cookie replay401, new Me200; tests retained.
5. Test assumed GET Login/Logout always405, but MVC fallback returns404; accept404/405 only, assert no cookies/mutation.
   Did not weaken CSRF, cookie flags or routing to satisfy a test.
6. First mutation-copy run lacked SEC-001 linked fixture source and failed compilation: not counted as a detected mutation.
   Copied the dependency and solution, reran to observe three actual assertion failures, restored all mutations then11/11 passed.
7. Final review added custom webroot/key-directory rejection and canonical same-origin default-port comparison,
   plus HTTP cookie-validation idle/absolute expiry tests. Focused suite98/98 and final full suites535/535 pass.
8. Secret scan flagged two deliberately fake placeholder connection strings and the fetch credentials-mode example.
   Added narrow test-line markers with explanations and reworded the fetch prose; did not disable/loosen the scanner.

## Deliverables / remaining gates

| Deliverable / DoD | Status |
|---|---|
| D1 DB/service/verifier | Implemented and tested against new BC-LocalAI MySQL, not legacy/company integration |
| D2 Login/Logout/Me | Implemented per SEC-001, plus CSRF bootstrap |
| D3 configuration | Implemented, disabled by default; policy numbers and production keys/TLS/grants need operational sign-off |
| D4 automated tests | Unit/test-host suite + explicit real MySQL suite; local results above |
| D5 security report | This file; review evidence is not independent reviewer approval |
| D6 BE-006/FE-003 documentation | [Configuration / API / remaining boundaries](BE-005-INTEGRATION.md) |
| Existing Chat regression | Automated regression passes; no Chat/AI service/UI edits |
| Independent code review / final security policy | **Pending**; do not close #21 or claim production-ready |

BE-006 follow-up: account status/stamp revalidation of existing sessions, Chat auth/CSRF/SSE enforcement,
durable/shared session/attempt store and lockout persistence as required, all-device revocation, Admin scripts/audit/retention.
No writer connection or grants added here. In-memory limiter/session store is single-process and resets at restart.
Current Chat remains anonymous; auth endpoints alone do not secure the whole application.

Live Ollama was checked at127.0.0.1:11434 and was unavailable; live Qwen/browser/160-case E2E/benchmark **Not Run**.
Do not mistake the mock AI regression for live model verification. No production deployment, PR/Issue edits or main merge authorized/performed.
Push is limited to `watzapon_BE-005` after final local checks, per user instruction.
