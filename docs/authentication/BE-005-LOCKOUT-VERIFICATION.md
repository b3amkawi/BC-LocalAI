# BE-005 — Persisted Attempts / Lockout Verification

2026-10-21 · branch `watzapon_BE-005` · Refs [#21](https://github.com/b3amkawi/BC-LocalAI/issues/21)

## Scope and decisions

Continues `dc477f79863dcd2eccd99f15f17cf55254d9929f`, based on pulled main
`8bffcc812cf268df874eacd9b97b022ea93a69bc`. Fetch during final review found no newer remote main.
This completes the missing persisted attempt writer; the [earlier report](BE-005-IMPLEMENTATION-VERIFICATION.md)
is historical, not the current implementation/results.

SEC-001 selected BC-LocalAI-owned MySQL accounts and the framework IdentityV3 verifier, not a company legacy adapter.
See [scope decisions](../security/legacy-auth-review.md). No company source, hash algorithm or real employee data was invented/tested.

The user/watzapon confirmed in this task's chat: 5 consecutive wrong passwords lock for 15 minutes;
success resets failures; retain 5/account/minute, 30/IP/minute, 120/global/minute and 2 simultaneous verifications.
This confirms policy numbers, not independent code review or production authorization.

The user subsequently authorized the small existing AI benchmark regression fix after initially asking to report it separately.
Its separate [before/after report](../BE-005-BENCHMARK-REGRESSION.md) records that scope extension.
Latest Git instruction: **commit locally only; do not push**. No PR, Issue mutation, main merge or deployment is included.

## Implementation / recovery

- Reader remains a fixed parameterized SELECT under `bc_auth_reader`; only the required security stamp was added to its projection.
- `IAuthenticationAttemptStore` / `MySqlAuthenticationAttemptStore`: separate connection, bounded command/cancellation,
  parameterized conditional single-row UPDATE, no retry, safe dependency error without raw inner exception.
- Writer has SELECT only on `id,is_active,security_stamp,access_failed_count,lockout_end_utc` and UPDATE only on
  `access_failed_count,lockout_end_utc,updated_at_utc`. It cannot read hashes or change credentials/role/status/stamp.
- UUID/stamp/active/unlocked predicates are checked under the InnoDB row lock. Count is calculated before expiry
  using MySQL's single-table left-to-right assignment semantics. Found-row semantics are required by configuration.
- Failure 5 sets database-UTC expiry +15 minutes; active lock cannot be cleared/extended by a login attempt.
  After expiry a wrong password starts count1; correct credentials clear count0/expiryNULL if predicates still match.
- Unknown/ineligible accounts perform one bounded dummy KDF and a null-id/stamp non-matching writer roundtrip.
  Stale, disabled or deleted accounts cannot authenticate; failed/unavailable writer cannot issue a session.
- Validate distinct reader/writer username AND secret, same server/port/database, safe pools/timeouts and TLS.
  Production requires VerifyFull; the explicit Development exception is encrypted literal loopback only.
- `AuthenticationRegistration.cs` adds the scoped attempt-store registration only. This round does not modify
  `AuthenticationController.cs` or `BasicAuthenticationTicketStore.cs`.
- Fixture generator has an opt-in writer flag. Existing SEC-001 defaults are preserved; dedicated `be005-mysql`
  is restricted to loopback3319. No table schema/hash migration or production account provisioning was performed.

Disable `Authentication:Enabled` and restart to roll back activation; sessions are invalidated on restart.
Do not erase counters to roll back: attempt metadata intentionally survives application/database restarts.
New writer configuration is required before enabling; missing/unsafe writer fails startup, not silently bypassing persistence.
Detailed [configuration, grant contract and handoff](BE-005-INTEGRATION.md).

## Executed tests

Environment: Windows, .NET SDK10.0.402 / net10.0, Node22.23.2, Python3.11.9, PowerShell7.6.5, local MySQL8.0.
These are correctness/mock tests, not measurements of Qwen performance on either reference or test hardware.

| Check | Observed result |
|---|---|
| Release build `BC-LocalAI.Tests`, --no-restore | 0 warnings /0 errors |
| Focused authentication tests | 109 passed /0 failed /0 skipped |
| Full .NET Development | 546 passed /0 failed /0 skipped; 33s |
| Full .NET Production | 546 passed /0 failed /0 skipped; 28s |
| Node `node --test 'tests/ui/*.test.mjs'` | 61 passed /0 failed /0 skipped |
| Explicit real MySQL project | 12 passed /0 failed /0 skipped; final run11s |
| Real MySQL restart | Persisted synthetic account count1 before stop and1 after start |
| Fixture Verify after restart/tests | 11/11 passed; Stop succeeded, fixture data retained |
| Mutation: broaden writer grant | Unit assertion failed /exit1; restored and rebuilt: 1/1 passed |
| Mutation: remove stamp and active-lock predicates | 2 selected real DB tests failed /exit1; restored and rebuilt: 2/2 passed |
| Initial full Python regression | 141 run, 1 failure: existing resource-sampling test; reproduced in isolation |
| New deterministic benchmark regression before fix | 1 run, 1 failure, count0 instead of1 /exit1 |
| New benchmark regression after fix | 1/1 passed; original failing case also passed three consecutive reruns |
| Final full Python regression after test cleanup | 143 passed /0 failed /0 skipped, 205.865s; no ResourceWarning in final log |
| Live Ollama smoke | Ollama0.40.6; qwen3:4b; HTTP200, done=true, done_reason=stop; script exit0 |
| Live Production backend smoke | Dedicated loopback5935; POST Chat200, qwen3:4b, Thai API explanation; GET home200; owned app stopped afterward |

Commands:

```powershell
dotnet build BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-restore
dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-build
$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-build
node --test 'tests/ui/*.test.mjs'
python -I -X utf8 -m unittest discover -s tests -p 'test_*.py'
$env:BE005_MYSQL_PRIVATE_DIRECTORY = Join-Path (Get-Location) '.local/be005-mysql/private'
dotnet test tests/BE005.MySqlTests/BE005.MySqlTests.csproj -c Release
```

For repeatable local DB tests, Start/test/Stop in one controlling command, Stop in finally; use Start for the existing fixture,
never re-Initialize/reset existing data. Raw final logs are ignored `artifacts/be005-lockout-verification/`.
Do not publish private fixture files, credentials, hashes, Data Protection keys or local database logs.

## Security and state-transition evidence

| Requirement | Test evidence |
|---|---|
| Durable failures/expiry/reset | `FifthFailureLocksForFifteenMinutesAndCorrectPasswordCannotClearIt`, `ExpiredLockStartsAtOneAndSuccessfulLoginClearsTheCounterWithoutChangingHash` |
| Concurrent correctness | `ConcurrentFailuresCannotLoseIncrementsOrBypassTheThreshold`: 20 requests, exactly5 matched, count5, lock set |
| Stale/disabled/deleted/unknown | `StaleStampDisabledDeletedAndUnknownAccountsCannotBeMutatedOrAuthenticated`: no unintended counter reset or match |
| Least privilege | `AttemptWriterCannotReadHashesChangeIdentityOrInsertDeleteAccounts`: actual MySQL1142/1143 denials; reader UPDATE also denied |
| Fail closed / cancellation | `WriterFailureAndCancellationFailClosedWithoutRawConnectionDetails`, `RepeatedSuccessfulResetStillReportsMatchedRowAndUnavailableWriterPreventsLogin`, blocked-query timeout/cancellation |
| HTTP across fresh app hosts | Five wrong Login requests → fresh host correct password401; fixture-admin expiry → fresh host Login200/reset0 |
| API/session/CSRF/input/privacy regressions | Existing and updated authentication test-host tests in both full .NET environments; real MySQL Login/Me/Logout flow |

## Findings and boundaries

1. Port3318 was occupied by another live instance: left untouched; created an isolated fixture on3319 instead.
2. A fixture process disappeared between command calls without a proven cause in available logs. Tests use an explicit
   Start/test/Stop lifecycle in one command; subsequent live suite and database restart checks passed. No machine-wide service was stopped.
3. A grant assertion used culture-sensitive substring matching and falsely rejected the exact expected SQL. Changed only that
   comparison to Ordinal; exact grant assertions and actual negative DB grant tests remain. No permissions were broadened to pass a test.
4. The unrelated AI benchmark had a reproducible final-sample race. Fixed only after explicit authorization; see separate report.
5. Python's existing CSV/socket ResourceWarnings identified missing test cleanup. CSV reads now use context managers;
   the CI mock server explicitly closes its socket and joins its thread. A new cleanup regression failed on the old code
   (open descriptor, expected -1) and passes with cleanup. No warning filters were added; see separate report for final execution.

Account lockout can be abused for denial of service; that trade-off is inherent in the approved policy. No rigorous statistical
timing-equivalence or distributed-rate-limit claim is made. Admin credential/status/role changes must rotate security_stamp.

**BE-006 integration is not performed/tested in this worktree.** Read-only inspection found BE-006 branch `7f9c3d9` replaces
the controller/session registration and deletes the basic ticket store. Preserve those changes when integrating later;
add only the new attempt-store DI registration to its registration. Do not restore the basic store or overwrite the BE-006 controller.
No BE-006 files/worktree were edited, merged or checked out by this completion round.

In this standalone BE-005 branch Chat endpoints remain anonymous; existing sessions are not account-revalidated.
BE-006/FE-003 remain separate deliverables, not a reason to claim an unimplemented writer is complete.
Authentication stays disabled by default. Ollama became available during final checks, so the direct model and backend Thai
smoke checks above were added. These limited calls do not replace the full160-case live evaluation or browser interaction.
Browser interaction, full160-case Qwen E2E, production TLS/deployment and independent human review are not verified here.
The smoke app used HTTP loopback with authentication disabled; its missing HTTPS redirect-port warning is not production TLS evidence.
[Acceptance / self-review checklist](BE-005-REVIEW-CHECKLIST.md).

## Final-check record

Final Python log: `artifacts/be005-lockout-verification/python-clean-final.log` (143 tests, OK /exit0).
The initial failing log and first142-test post-fix run are retained separately, not overwritten to hide failures.
Final checks include the real log-based test-count gate, tracked/staged secret scan, local Markdown file-link check,
PowerShell parser checks and `git diff --cached --check`; their observed results are recorded at completion below.
No credentials/private DB files or generated build/mutation artifacts are staged. No GitHub Actions result is claimed.

| Final hygiene check | Observed result |
|---|---|
| Actual log-based test-count gate | .NET546 >=546, Node61 >=23, Python143 >=143; zero failures/skips; exit0 |
| Final Python warning/failure scan | No ResourceWarning, Traceback or FAILED in final143-test log |
| Secret scan | 324 tracked/staged files checked, clean; not a Git-history scan |
| Local Markdown links | 63 Markdown files checked, OK; external URLs/anchors not verified by this gate |
| PowerShell parser | local-mysql.ps1, BenchmarkCommon.psm1 and test fixture: all3 passed; CRLF/no BOM preserved |
| Whitespace | `git diff --cached --check` passed |
| Protected source / session files | Schema, Chat/Ollama/UI/workflow unchanged from pulled main; controller/basic ticket store unchanged from starting BE-005 HEAD |

Local commit only, per the user's latest Git instruction. Push, PR/Issue changes, merge main and merge BE-006 are not performed.
