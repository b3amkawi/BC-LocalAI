# SEC-001 - Preparation (before the legacy login code is available)

Issue: #20 · Status: **preparation only. This is not the security review.**

> อัปเดต 2026-10-20: ผู้ใช้อนุญาตให้สร้าง MySQL/บัญชีพัฒนาใหม่เองเมื่อยังไม่มีข้อมูล Legacy
> และเลือก native MySQL บน Windows ไม่ใช้ Docker/WSL ดู [ผลตรวจและขอบเขตใหม่](legacy-auth-review.md)
> และ [วิธีใช้งาน/หลักฐาน](SEC-001-VERIFICATION.md) ข้อมูลใหม่นี้ไม่ใช่หลักฐานตรวจระบบบริษัท
> Acceptance Criteria ด้าน Legacy และการอนุมัติ Security Review ใน Issue #20 ยังไม่ผ่าน

SEC-001 has to review the company's real login source code, the custom password hash and the MySQL user schema. None of these are in
this repository, so the review itself cannot start yet. This document lists what is needed, how to hand it over safely, and the checklist the
review will follow, so the work can start as soon as the inputs arrive. Nothing here describes the real company system.

## 1. Inputs needed (issue section 9, "Required")

| # | Input | Who provides it | Form |
|---|-------|-----------------|------|
| 1 | Source code of the current web login (request handling, user lookup, password check, session creation, lockout, logging) | system owner | read access to the repository or an export; **not** committed to BC-LocalAI |
| 2 | Source of the custom password hash function (create + verify) | system owner | as above |
| 3 | MySQL schema of the user tables (`SHOW CREATE TABLE`), without data | DBA | DDL only |
| 4 | Authorized test accounts in a **non-production** copy, with their hash values | DBA / security | separate secure channel; never in Git, chat or tickets |
| 5 | Company security requirements (password policy, session lifetime, lockout, audit, data classification) | security / IT | document |
| 6 | Who approves the review result (issue: "Security Review passed and approach approved") | project owner | name / role |

**Rules for handling:** no real password, real hash, connection string or employee data may appear in this repository, in logs, in issue
comments or in screenshots (issue section 8). Test vectors in documents must be generated from made-up passwords.

## 2. Review checklist (issue sections 4 and 10)

### 2.1 Password hash

- [ ] Algorithm (for example MD5, SHA-1, SHA-256, bcrypt, PBKDF2, custom) and how it is combined (order of salt, pepper, password)
- [ ] Salt: per user? random? length? where stored?
- [ ] Work factor / iterations (none = fast hash = weak)
- [ ] Password encoding before hashing (UTF-8? ANSI code page? Thai characters?) and normalisation (trim, case)
- [ ] Output format (hex / Base64, upper/lower case, prefix or version marker)
- [ ] Comparison: constant time or `==`?
- [ ] Empty / null password and maximum length behaviour
- [ ] Reproduce with made-up passwords: write test vectors that the BC-LocalAI adapter (BE-005) must match exactly

### 2.2 Login flow

- [ ] User lookup: parameterized query? case sensitivity of the username?
- [ ] Different messages or timing for "unknown user" vs "wrong password" (user enumeration)?
- [ ] Account status (active / disabled / locked / expired) checked before or after the password?
- [ ] Failed-attempt counting and lockout; reset conditions
- [ ] Session creation: identifier randomness, cookie flags, lifetime, regeneration after login
- [ ] Logging: are passwords, hashes or session ids written anywhere?

### 2.3 Schema

- [ ] Stable user identifier for BC-LocalAI (needed by BE-007 `Conversations.UserId`)
- [ ] Fields BC-LocalAI really needs (minimum: id, display name, status, role/department if used); everything else is not read
- [ ] A read-only database account limited to those columns / a view

## 3. Common weaknesses to look for, with the usual mitigation (to be confirmed against the real code)

| Finding | Risk | Usual mitigation (decision belongs to the review) |
|---------|------|---------------------------------------------------|
| Fast hash (MD5 / SHA-x), with or without salt | offline cracking if the table leaks | keep verifying the old format in the adapter, plan migration to a slow hash (PBKDF2 / bcrypt / Argon2) on next successful login; **migration needs DB write and is out of scope for SEC-001 / BE-005** |
| No salt or a shared salt | rainbow tables, identical passwords visible | as above |
| Non-constant-time compare | timing attack (low practical risk over a network, cheap to fix) | `CryptographicOperations.FixedTimeEquals` in the adapter |
| Different errors for unknown user / wrong password | user enumeration | one generic error, similar response time (dummy hash for unknown users) |
| No lockout / rate limit | password guessing | ASP.NET Core rate limiting on `/api/auth/login` per user and per IP (BE-005) |
| String-built SQL | SQL injection | parameterized queries only (BE-005) |
| Encoding differences (Thai passwords) | some users cannot log in, or two passwords hash the same | reproduce the exact encoding with test vectors |

## 4. Deliverables that will follow (issue section 8 file structure)

`docs/security/legacy-auth-review.md`, `authentication-architecture.md`, `authentication-api-contract.md`,
`authentication-risk-assessment.md`, `authentication-test-plan.md`: now contain a clearly labelled **new local-account proposal**
under the user's subsequent authorization. They do not claim the missing legacy inputs have been received or reviewed.

## 5. What can already be stated without the legacy code

- Proposed: BC-LocalAI will use the ASP.NET Core cookie authentication handler (no hand-made tokens), `HttpOnly`, `Secure`, `SameSite`, Data Protection keys
  stored outside the web root (BE-005/006 requirements).
- The legacy adapter is isolated behind an interface, so BE-005 can be unit-tested with test vectors and no database.
- The authentication cookie is never forwarded to Ollama (BE-006); Ollama stays loopback-only (AI-002 / BE-003).
