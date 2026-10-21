# SEC-003 — Verification report: Admin account management script

Branch `watzapon_SEC-003` from `main` babbd61 (after PR #35, BE-006). Issue: [#37](https://github.com/b3amkawi/BC-LocalAI/issues/37), PR #38.
Date: 2026-10-21. Machine: Windows 11, RTX 4060 Laptop 8 GB (GPU not used by this work), MySQL 8.0.46 native, .NET SDK 10.0.402.
Every result below comes from a run on this machine; nothing was run against a company or production database.

Two rounds: the first implementation (commit ae73725), then fixes after three specialist reviews (security code review,
identity/password policy, MySQL DBA) — section 2. Sections 1, 3 and 5 describe the current code.

## 1. Scope delivered

| Requirement (brief / issue #37) | Where | Evidence |
|---|---|---|
| create, disable, enable, reset password, set role (User/Admin), revoke all sessions | `tools/BcAdmin`, `scripts/security/bc-admin.ps1` | live tests 1–4, wrapper smoke run (section 4) |
| SEC-001 schema reused (`users`, IdentityV3 hash, `is_active`, `role`, `security_stamp`) | `AccountPolicy.HashPassword`, `MySqlAdminAccountStore` | hash checked by the app's `IdentityPasswordVerifier`; created accounts sign in through the real app |
| separate writer `bc_admin_writer`, column-level least privilege, never the reader | `AdminWriterGrants`, `database/sec003/admin-writer-grants.sql`, `DbConfig` | `SHOW GRANTS` live test; 15 denied statements; reader / attempt writer / fixture admin refused |
| credentials from a protected file or prompt, never repo/log/argument | `DbConfig.EnsurePrivate`, `AdminCli` | unit tests (ACL, `--password*` refused, `--password-stdin` refused at a console, no secret in output) |
| audit for every action: who, what, when, target id; no password/hash/stamp | `database/sec003/admin-audit.sql` (table + 2 triggers) | live: every column checked against password, hash, stamp; changes made **without** the script are audited too |
| password reset and disable always rotate `security_stamp` | `ChangeAsync` (every change rotates) | live tests + mutations |
| Admin can never read other users' chats | grants on `users` only, nothing on any other table | live: writer refused on a stand-in chat table |
| tests: unit + live against isolated fixture, data restored in `finally` | `BC-LocalAI.Tests/Admin`, `tests/SEC003.AdminTests` | section 3 |

Design decisions (current):

- **.NET console tool, not pure PowerShell**: the hash must be byte-compatible with `IdentityPasswordVerifier`; same framework
  `PasswordHasher` and same MySQL driver/version as the app. No new third-party dependency.
- **The database writes the audit** (AFTER INSERT / AFTER UPDATE triggers on `users`), not the script. Any change to the password,
  status, role or stamp is recorded, whoever makes it; the writer has **no privilege on `admin_audit_log`**; `db_user` is `USER()` taken
  by the server. `actor` / `os_user` come from session variables the script sets (`(direct SQL)` / `(unknown)` otherwise) and can be
  faked by someone holding the writer's credentials — `db_user` cannot.
- **Change and audit in one statement/transaction**: a failing audit insert fails the change (strict `sql_mode` captured by the
  triggers; tested with an over-long value, error 1406). The script also refuses to commit if no audit row was produced.
- **Every change rotates the stamp** (also `enable` and `set-role`). The trigger records what actually changed, one row per kind.
- **Issue ID SEC-003**: SEC-002 and BE-008 are already used by the Phase 3 drafts in `docs/planning/phase-3/`.

## 2. Review round (2026-10-21) — findings and fixes

Three reviews were run by specialist AI agents (reports in the session's shared notes, not in the repo). The owner chose which to fix:
B1, S1–S5, the DBA items below and the recommended password values.

| Finding | Source | Fix | Test that proves it |
|---|---|---|---|
| **B1** audit enforced only by the script: with the writer's credentials the `mysql` client could change accounts without audit, and `db_user` was a writable column (docs wrongly said it could not be forged) | security review | triggers write the audit; writer has no privilege on `admin_audit_log`; `db_user = USER()`; docs corrected | live `ChangingAnAccountWithoutTheScript_IsStillAudited_WithTheRealDatabaseUser`; writer INSERT/UPDATE/DELETE on audit and DROP TRIGGER refused |
| **S1** connection lost during COMMIT reported "Nothing changed", exit 5 | security review | commit errors → `AdminOutcomeUnknownException` → exit **6** "may or may not be saved" | unit `ConnectionLostDuringCommit_Is6_AndNeverSaysNothingChanged` |
| **S2** `admin-writer-grants.sql` deliverable missing; `REQUIRE SSL` only in a comment | security review | file added; `ALTER USER … REQUIRE SSL …` printed as a real statement; CREATE USER stays a comment (placeholder secret) | unit `GrantsFile_ForTheDba_IsExactlyWhatTheScriptPrints`, `PrintGrants_…` |
| **S3** `--password-stdin` at a console echoes the password | security review | refused when stdin is a terminal | unit `PasswordStdin_AtAConsole_IsRefused_…` |
| **S4** `reset-password --user <id>` skipped the username rule | security review | account looked up first, rule applied to its `normalized_username` | unit `ResetPassword_ById_StillRefusesAPasswordContainingTheUsername` |
| **S5** loopback allowed without certificate checks in every environment (app: Development only) | security review | needs `ssl-ca` (VerifyFull) unless `--local-dev-database` on loopback | unit `DbConfig_Loopback_…`, `LoopbackWithoutTheLocalDevFlag_…` |
| `SELECT (is_active, role)` granted but never needed | DBA review | removed; writer SELECT = `id, normalized_username` | live `SHOW GRANTS`; `SELECT is_active, role` refused |
| audit table without `ENGINE` / charset | DBA review | `ENGINE = InnoDB`, `utf8mb4` / `utf8mb4_0900_bin` | unit `AuditSql_…` |
| rollback relied on server strict mode | DBA review | session `sql_mode` strict in the script; triggers created under strict mode | live rollback tests; unit checks the `SET SESSION sql_mode` line |
| writer host `%` allowed | DBA review | `%` and `_` wildcards refused; fixed IP or netmask only | unit `WriterGrants_RefuseAnythingButAPlainAccountAndHost` |
| writer account had no resource / lockout limits | DBA review | `WITH MAX_USER_CONNECTIONS 4 FAILED_LOGIN_ATTEMPTS 5 PASSWORD_LOCK_TIME 1` | unit `WriterAccount_RequiresTls_…`; fixture applies it |
| minimum 12 counted UTF-16 units | policy review | **15 code points** (owner decision) | unit `CheckPassword_CountsCodePoints_NotUtf16Units`, boundaries 14/15 |
| username rule for any length | policy review | substring rule from 4 characters; shorter usernames only as the whole password | unit `CheckPassword_UsernameRule_AppliesFromFourCharacters` |

Defect found while fixing: the fixture's init file stopped silently at a wrong clause order in `ALTER USER` (resource options must
precede the lock options), leaving the writer without grants. Order fixed, and the fixture Verify now has
`admin_writer_can_resolve`, which fails when the writer's grants are missing. Also, the audit SQL needs the `mysql` client
(`DELIMITER`), so the fixture applies the DBA's own file after the first start instead of through `--init-file`.

Not taken in this PR (follow-up issues recommended by the policy review): offline common-password list, NFC normalization on both
sides, self-service password change + must-change flag, cap of 100 consecutive failed logins (BE-005), MFA for Admin, hash benchmark
at deploy. DBA open questions (production `users` table exists? version ≥ 8.0.29, backup/PITR, per-admin accounts, audit retention
and reader) remain for the owner/DBA.

## 3. Tests actually run (after the review fixes and merging `main` d121f50)

`main` gained BE-007 chat history (PRs #36/#39) while this PR was open. It was merged in (no rebase); the `sec003-mysql` fixture now
also holds the real BE-007 tables (`Conversations`, `Messages`) and accounts (`bc_history_rw`, `bc_history_purge`), so the
"cannot read chats" checks run against the real chat tables, and both BE-007 accounts are refused by the script.

| Suite | Result |
|---|---|
| `dotnet test BC-LocalAI.slnx` (unit + integration, 78 SEC-003 unit tests) | **913** passed, 0 failed, 0 skipped |
| `node --test "tests/ui/*.test.mjs"` | 61 passed |
| `python -m unittest discover -s tests` | 143 passed |
| `check_test_counts.py` (baseline: main 835 + 78 = 913) | all gates passed |
| `scan_secrets.py` (files staged) / `check_doc_links.py` | clean / OK |
| Fixture `sec003-mysql` rebuilt, Initialize + Verify (127.0.0.1:3318) | **16/16** checks (11 SEC-001 + audit table, 2 triggers, writer grants applied, writer cannot read hash, writer cannot read audit) |
| Live `tests/SEC003.AdminTests` against `sec003-mysql` (with BE-007 tables) | **36** passed |
| Regression: `be005-mysql` (3319) + live `tests/BE005.MySqlTests` | **16** passed |
| Regression: fresh `be007-mysql` (3320, 11/11 checks) + live `tests/BE007.MySqlTests` | **53** passed, 1 skipped (real-model check, skipped by default as in BE-007's report) |

Live tests (`AdminScriptLiveTests`), real admin writer + real app login:

1. create → signs in through the app → `disable` → open session 401, login refused, stamp changed, hash unchanged → `enable` → login works
2. `reset-password` → open session 401, old password refused, new password works, hash verifies, status/role unchanged
3. `set-role Admin` → open session 401, new login reports role `Admin`
4. `revoke-sessions` → two sessions 401, only the stamp changed (all other columns equal)
5. audit row: actor, OS user, `db_user = bc_admin_writer@127.0.0.1`, action, target id, time window; no column contains the password, hash or stamp
6. audit insert fails (disable / reset-password / create) → account unchanged / not created, no audit row
7. **writer changes an account with plain SQL and a fake actor** → still one audit row per change with the real `db_user`; without the
   script's variables `actor = (direct SQL)`
8. BE-005 failed-login bookkeeping by the attempt writer → no audit row
9. duplicate username (other case) → exit 4, still one account, no audit row
10. unknown username / id → exit 3, no audit row
11. `reader.cnf`, `writer.cnf` (attempt writer), `history.cnf` / `purge.cnf` (BE-007), `admin.cnf` (fixture admin) → refused, exit 5
12. `SHOW GRANTS` equals exactly USAGE + one column-scoped grant on `users`
13. writer refused (1142/1143): read hash / stamp / status+role / `SELECT *`, read / insert / update / delete audit, drop trigger,
    delete users, update `access_failed_count` / `username`, read the real BE-007 `Messages` / `Conversations` tables, update
    `Messages`, read a stand-in chat table, create table, read `mysql.user`

Mutation check, review round (each bug introduced, tests run, file or trigger restored):

| Mutation | Tests that failed |
|---|---|
| T1 update trigger missing in the database | 8 live |
| T2 script does not set the audit context | 4 live |
| U1 commit failure reported as "nothing changed" (exit 5) | 1 unit |
| U2 `--password-stdin` allowed at a console | 1 unit |
| U3 reset by id skips the username rule | 1 unit |
| U4 host `%` / `_` allowed | 3 unit |
| U5 loopback without `--local-dev-database` | 2 unit |
| U6 minimum counted in UTF-16 units | 1 unit |

First round (still valid): M1 disable without stamp rotation (1 live), M3 reset without rotation (1 live), M4 `--password` accepted
(4 unit), M5 writer may SELECT `password_hash` (1 unit), M6 reader allowed (2 unit), M7 ACL not checked (1 unit) — each failed tests.
(M2 "audit after commit" no longer applies: the audit is written by the trigger inside the statement.)

After every run the fixture held 3 accounts and 0 audit rows. First-round defects (test-side `Guid` cast, 8 leaked test accounts
removed, `--nologo` forwarded by `dotnet run`) are fixed as described in commit ae73725's report.

## 4. Wrapper smoke run (real process, stdin password)

`create … --local-dev-database --password-stdin` → `OK create … audit=68`; `reset-password` → `OK … audit=69`; `disable` →
`OK … audit=70`; audit rows `create` / `reset_password` / `disable` with `actor = smoke`, `db_user = bc_admin_writer@127.0.0.1`.
Same command without `--local-dev-database` → exit 5 ("Add 'ssl-ca' …"). `print-grants` prints a commented CREATE USER with a
`<secret>` placeholder, the `ALTER USER … REQUIRE SSL …` and the 3 grants. Test account and audit rows deleted afterwards.

## 5. Risks and remaining work

| # | Item | Status |
|---|---|---|
| 1 | Whoever holds the writer's credential file can create an Admin or reset any password (audited, with real `db_user`) | protect the file (ACL check, TLS, fixed host, MySQL lockout); one MySQL account per Admin recommended |
| 2 | `actor` / `os_user` can be faked by a writer-credential holder | `db_user` from `USER()` cannot; documented |
| 3 | DBA or the trigger definer can still change or delete audit rows; no retention or export | out of scope (OPS-002); DBA review recommends export to append-only storage |
| 4 | Audit table, triggers and grants not applied to any real database | DBA follows guide §3; production `users` table (SEC-001) must exist first; MySQL ≥ 8.0.29 |
| 5 | Password policy follow-ups (common-password list, NFC, must-change on first login, 100-failure cap, Admin MFA) | follow-up issues |
| 6 | No unlock and no delete command | by design |
| 7 | No guard against removing the last Admin | does not block recovery (script uses MySQL credentials, not app roles) |
| 8 | Live suites are not in CI (need the local fixture), same as BE-005 | run locally per this report |
| 9 | Interactive hidden prompt (`ConsolePasswordSource`) is not covered by automated tests | manual check recommended |
| 10 | Triggers with binary logging on may need `log_bin_trust_function_creators` or SUPER for the definer | DBA decision (guide §3) |

Not done (needs the owner's word): merge.
