# SEC-003 — Verification report: Admin account management script

Branch `watzapon_SEC-003` from `main` babbd61 (after PR #35, BE-006). Issue: draft only, not yet created on GitHub.
Date: 2026-10-21. Machine: Windows 11, RTX 4060 Laptop 8 GB (GPU not used by this work), MySQL 8.0 native, .NET SDK 10.0.402.
Every result below comes from a run on this machine; nothing was run against a company or production database.

## 1. Scope delivered

| Requirement (brief / issue draft) | Where | Evidence |
|---|---|---|
| create, disable, enable, reset password, set role (User/Admin), revoke all sessions | `tools/BcAdmin`, `scripts/security/bc-admin.ps1` | live tests 1–4, wrapper smoke run (section 4) |
| SEC-001 schema reused (`users`, IdentityV3 hash, `is_active`, `role`, `security_stamp`) | `AccountPolicy.HashPassword`, `MySqlAdminAccountStore` | hash checked by the app's `IdentityPasswordVerifier`; created accounts sign in through the real app |
| separate writer `bc_admin_writer`, column-level least privilege, never the reader | `AdminWriterGrants`, `DbConfig` | `SHOW GRANTS` live test; 12 denied statements; reader / attempt writer / fixture admin refused |
| credentials from a protected file or prompt, never repo/log/argument | `DbConfig.EnsurePrivate`, `AdminCli` | unit tests (ACL, `--password*` refused, no secret in output) |
| audit for every action: who, what, when, target id; no password/hash/stamp | `database/sec003/admin-audit.sql`, `AuditAsync` | live test: all columns checked against password, hash and stamp |
| password reset and disable always rotate `security_stamp` | `ChangeAsync` (every change rotates) | live tests + mutation M1, M3 |
| Admin can never read other users' chats | grants on `users` + `admin_audit_log` only | live: writer refused on a stand-in chat table (`1142`) |
| tests: unit + live against isolated fixture, data restored in `finally` | `BC-LocalAI.Tests/Admin`, `tests/SEC003.AdminTests` | section 3 |

Design decisions (review welcome):

- **.NET console tool, not pure PowerShell**: the hash must be byte-compatible with `IdentityPasswordVerifier`; the tool uses the same
  framework `PasswordHasher` and the same MySQL driver/version as the app. No new third-party dependency.
- **Change and audit in one transaction**: if the audit row cannot be written the change is rolled back (tested by forcing the audit
  INSERT to fail with an over-long value, strict mode error 1406).
- **Every change rotates the stamp** (also `enable` and `set-role`), not only reset and disable: one simple rule, and BE-005's docs
  already ask for it on every admin change.
- **`db_user` = `CURRENT_USER()`** is recorded by MySQL, so the audit names the database account even if `--actor` is wrong.
- **Writer cannot read the audit log** (INSERT only) nor `password_hash` / `security_stamp`. The SELECT on four columns is needed
  because MySQL requires it for columns used in `WHERE`.
- **Issue ID SEC-003**: SEC-002 and BE-008 are already used by the Phase 3 drafts in `docs/planning/phase-3/`.

## 2. Files

New: `tools/BcAdmin/*` (6 source files + csproj), `database/sec003/admin-audit.sql`, `scripts/security/bc-admin.ps1`,
`BC-LocalAI.Tests/Admin/AdminScriptTests.cs`, `tests/SEC003.AdminTests/*`, `docs/security/SEC-003-ADMIN-GUIDE.md`, this report.
Changed: `tools/Sec001Fixtures` (opt-in `sec003-admin-writer` flag; grants taken from `AdminWriterGrants`, so documented and tested
grants cannot drift), `scripts/security/local-mysql.ps1` (`-InstanceName sec003-mysql`, port 3318, 3 extra Verify checks),
`BC-LocalAI.slnx` (tool built and unit-tested in CI), `BC-LocalAI.Tests.csproj`, `docs/devops/test-baseline.json` (652 → 713),
`README.md` and `BE-006-DEVELOPER-GUIDE.md` (one pointer each). App code (`BC-LocalAI/`) is **not** changed.

## 3. Tests actually run

| Suite | Result |
|---|---|
| `dotnet test BC-LocalAI.slnx` (unit + integration, includes 61 new SEC-003 unit tests) | **713** passed, 0 failed, 0 skipped |
| `node --test "tests/ui/*.test.mjs"` | 61 passed |
| `python -m unittest discover -s tests` | 143 passed |
| `check_test_counts.py` with the new baseline | all gates passed |
| `scan_secrets.py` (files staged) / `check_doc_links.py` | clean / OK (one fake test value marked `ci-allow-secret`) |
| Fixture `sec003-mysql` Initialize + Verify (127.0.0.1:3318) | **14/14** checks (11 SEC-001 + audit table, writer cannot read hash, writer cannot read audit) |
| Live `tests/SEC003.AdminTests` against `sec003-mysql` | **26** passed |
| Regression: fresh `be005-mysql` fixture + live `tests/BE005.MySqlTests` | see section 5 |

Live tests (`AdminScriptLiveTests`), real admin writer + real app login:

1. create → signs in through the app → `disable` → open session 401, login refused, stamp changed, hash unchanged → `enable` → login works
2. `reset-password` → open session 401, old password refused, new password works, hash verifies, status/role unchanged
3. `set-role Admin` → open session 401, new login reports role `Admin`
4. `revoke-sessions` → two sessions 401, only the stamp changed (all other columns equal)
5. audit row: actor, OS user, `db_user = bc_admin_writer@127.0.0.1`, action, target id, time window; no column contains the password, hash or stamp
6. audit INSERT fails (disable / reset-password / create) → account unchanged / not created, no audit row
7. duplicate username (other case) → exit 4, still one account, no audit row
8. unknown username / id → exit 3, no audit row
9. `reader.cnf`, `writer.cnf` (attempt writer), `admin.cnf` (fixture admin) → refused, exit 5
10. `SHOW GRANTS` equals exactly USAGE + the documented column grants
11. writer refused (MySQL error 1142/1143): read hash, read stamp, `SELECT *`, read/update/delete audit, delete users, update
    `access_failed_count` / `username`, read a stand-in chat table, create table, read `mysql.user`

Mutation check (each bug was introduced, tests run, file restored):

| Mutation | Tests that failed |
|---|---|
| M1 `disable` does not rotate the stamp | 1 live |
| M2 audit written after commit (not atomic) | 2 live |
| M3 `reset-password` does not rotate the stamp | 1 live |
| M4 `--password <value>` accepted as an argument | 4 unit |
| M5 writer may `SELECT password_hash` | 1 unit |
| M6 reader account allowed | 2 unit |
| M7 credential file ACL not checked | 1 unit |

Defect found and fixed during testing: the first live run failed 9 tests on a test-side cast (MySqlConnector returns `CHAR(36)` as
`Guid`), and 8 synthetic `sec003.test.*` accounts from that run stayed in the fixture because cleanup only knew ids recorded after
the failing line. Cleanup now records the username **before** creating the account; the 8 rows (isolated fixture, test namespace
only) were deleted and later runs leave the fixture at 3 accounts and 0 audit rows (checked after each run).
Also fixed: the wrapper passed `--nologo` to `dotnet run`, which forwards unknown options to the tool (refused, exit 2).

## 4. Wrapper smoke run (real process, stdin password)

`create` via `scripts/security/bc-admin.ps1 ... --password-stdin` → `OK create ... audit=73`, password not in output;
`revoke-sessions` → `OK ... audit=74`; audit rows `create` / `revoke_sessions` with `db_user = bc_admin_writer@127.0.0.1`;
account and audit rows deleted afterwards. `print-grants` prints the 4 grants and a `<secret>` placeholder only;
`--password hunter2…` → exit 2; reader config → exit 5.

## 5. Regression of BE-005 / BE-006 live suite

The fixture generator changed, so the BE-005 fixture was created fresh in this worktree (`be005-mysql`, port 3319, free before start)
and the existing live suite was run, then the instance was stopped.

| Step | Result |
|---|---|
| `Initialize -InstanceName be005-mysql -Port 3319` (new generator) | created; reader, attempt writer, admin; **no** admin writer, no audit table (flag not given) |
| `Verify` | 11/11 checks |
| `tests/BE005.MySqlTests` (BE-005 attempts/lockout + BE-006 account changes) | **16** passed — same count as BE-006-VERIFICATION §8 |
| `Stop` | stopped; MySQL80 service untouched |

## 6. Risks and remaining work

| # | Item | Status |
|---|---|---|
| 1 | Password policy (12–1024 characters, no username) is a draft | needs company / owner approval |
| 2 | `--actor` is typed by the operator | mitigated by `os_user` and `db_user`; one MySQL account per Admin recommended |
| 3 | A DBA (not the writer) can still change or delete audit rows; no retention or export | out of scope (OPS-002) |
| 4 | Grants and audit table not applied to any real database | DBA runs `database/sec003/admin-audit.sql` and `print-grants` output after approval |
| 5 | `reset-password --user <id>` cannot check "password contains username" (writer cannot read `username`) | accepted; check applies when `--user` is a username |
| 6 | No unlock and no delete command | by design (no grants on lockout columns / DELETE) |
| 7 | No guard against removing the last Admin | does not block recovery (script uses MySQL credentials, not app roles) |
| 8 | Connection lost during COMMIT → outcome unknown | message tells the operator to check the account and audit before retrying |
| 9 | Live suites are not in CI (need the local fixture), same as BE-005 | run locally per this report |
| 10 | Interactive hidden prompt (`ConsolePasswordSource`) is not covered by automated tests | tested only through the fake prompt and `--password-stdin`; manual check recommended |

Not done (needs the user's word): GitHub issue creation, push, pull request, merge.
