# SEC-001 — Test Methodology & Implementation Handoff

วันที่: 2026-10-20 · Expected outcomes กำหนดก่อนรัน; Actual results อยู่ [Verification](SEC-001-VERIFICATION.md)

## 1. Data / methodology

เฉพาะฐานใหม่ `bc_localai_sec001` ที่ 127.0.0.1:3307 และบัญชีสมมติจาก generator
ไม่ทดสอบ password บริษัท ไม่ทำ brute force หรือ fault injection บนบริการ MySQL80 เดิม
username สมมติ: sec001.active / sec001.disabled / sec001.locked; ทุกบัญชี User ไม่ใช่ Admin
password สุ่มคนละค่า เก็บเฉพาะ `.local/sec001-mysql-v2/private/test-accounts.json` ที่จำกัด Windows ACL และ Git-ignore
logs/temporary root password/generated SQL/hash ทั้งหมดอยู่ `.local/` ไม่แนบหลักฐานที่มี secret
แยก framework/fixture tests, local DB integration และ future API/session tests ไม่ใช้ผลกลุ่มหนึ่งแทนอีกกลุ่ม

## 2. Executable local verification

| ID | Expected | วิธี |
|---|---|---|
| F01 | 3 fixtures มี UUID/password/hash ต่างกันและไม่เป็น Admin | xUnit fixture tests + DB check |
| F02 | IdentityV3/HMAC-SHA512, 220k, salt >=16 bytes | ตรวจ framework hash header ใน memory ไม่ print hash |
| F03 | Exact Thai/emoji/newline/space password ผ่าน; wrong password ไม่ผ่าน; salt ไม่ซ้ำ | xUnit theory |
| F04 | Seed SQL ไม่ใส่ plaintext password และเก็บสถานะ disabled/locked | xUnit |
| F05 | Fixture SQL generator ปฏิเสธ uncontrolled values | xUnit negative cases |
| D01 | users count=3, disabled=1, locked=1, Admin=0 | local-mysql.ps1 Verify |
| D02 | ไม่มี plaintext-password column | information_schema |
| D03 | reader SELECT ได้, UPDATE no-op ไม่ได้ (1142) | least-privilege negative test |
| D04 | unknown username ไม่มีข้อมูล, duplicate key rejected (1062) | lookup + isolated transaction |
| D05 | instance bind loopback และ mysqlx disabled | server variables/plugins |
| D06 | Hash ที่อ่านจากฐานจริงยืนยันรหัสสุ่ม3บัญชีได้ และปฏิเสธรหัสผิดทั้ง3 | ส่ง hash ผ่าน memory/stdin ให้ framework verifier ไม่ print |
| L01 | Stop→Start ไม่ reset/seed ซ้ำ; countยัง3 | local instance lifecycle |
| L02 | Initialize ซ้ำต้อง fail ไม่ overwrite datadir/secret | fail-safe negative test |
| S01 | .local ถูก ignore, ACL ไม่มี inherited broad access | git check-ignore + Windows ACL metadata |

## 3. Tests that BE-005 must implement (Not Run here)

| Area | Cases / acceptance |
|---|---|
| Request | null/empty/oversize/malformed/duplicate fields, SQL payload, username case, Unicode password |
| Credential | active correct succeeds; wrong/unknown/disabled/locked same generic401; malformed hash fails safely |
| Repository | parameterized query, cancellation/deadline, timeout/connection loss503, no secret/raw SQL in logs |
| Hash | dummy hash for unknown, bounded CPU; old format only if reviewed; no silent fallback/rehash |
| Cookie/CSRF | valid bootstrap→Login, missing/invalid token403, rotated principal token, Secure/HttpOnly/Path/SameSite |
| Rate limit | per-user/IP plus global concurrency,429 + Retry-After, no unbounded per-username memory |
| API | Login/Me/Logout exact contract; no redirect HTML, no caching; existing Chat regression unchanged |

## 4. Tests that BE-006 must implement (Not Run here)

- anonymous401, wrong-role403, User/Admin permission matrix approved before tests
- idle/absolute expiry using controllable clock; renewal cannot bypass absolute limit
- replay cookie after Logout/revoke/disable/delete/password/role changes must fail
- cross-user ownership and forged body/user ID/roles must not grant access
- DB/session-store unavailable fail closed before AI work
- open SSE cancellation on revoked/expired session within approved deadline; no completed event on cancelled work
- Data Protection persisted keys/restart and isolated-session concurrency tests
- audit metadata/correlation only; never cookie/password/hash/employee PII in logs

## 5. SEC-001 original Acceptance Criteria traceability

| Original AC | Evidence / current disposition |
|---|---|
| Existing Login Source reviewed | Blocked — source not supplied |
| Identify Custom Hash algorithm | Blocked — IdentityV3 fixture is not legacy algorithm |
| Salt/iterations/encoding/compare reviewed | Blocked for Legacy; framework fixtures verified separately |
| Company MySQL Schema reviewed | Blocked — new local schema is not company schema |
| Account status handling reviewed | Local data states only; real Login logic Not Run |
| Legacy Adapter Design | Conceptual boundary in Architecture; implementation-specific details blocked |
| Authentication Flow Diagram | Proposed in Architecture |
| Session Design | Proposed; policy approval pending |
| Authorization Model | Proposed; no enforcement implemented |
| API Contract | Proposed; no endpoints implemented |
| Threat Assessment | Proposed Risk Assessment; not a source-backed Legacy vulnerability finding |
| Brute force / SQL injection mitigation | Design/test requirements; local DB read-only negative check |
| CSRF / Session hijacking mitigation | Design/test requirements only |
| Disabled / session revocation policy | Proposed; approved bound and implementation pending |
| Test Plan BE-005/006 | This document; future integration cases explicitly Not Run |
| Weak Hash Migration recommendation | Conditional in Review/Risk Assessment; no evidence of weak Legacy hash |
| No Sensitive Data in repository | New artifacts checked; not a claim that all Git history was scanned |
| Security Review approved | Pending — no sign-off obtained |
| Follow AGENTS.md | Scoped branch, isolated instance, tests/evidence, no production change/push/merge |

## 6. Handoff / approval gate

Do not mark original SEC-001 Done or unlock company authentication based on synthetic data alone.
Leader must confirm revised local-account scope vs Legacy integration and approve security design/API/policies.
BE-005 must resolve the existing read-only requirement versus new-account lifecycle/lockout writes explicitly.
Full automated Authentication Integration Tests belong to BE-005/006; this issue supplies plan and local evidence only.
