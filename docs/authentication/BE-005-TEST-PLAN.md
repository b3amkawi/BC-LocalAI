# BE-005 — Test Plan / Handoff

วันที่ 2026-10-20 · Expected outcomes สำหรับเตรียม implementation; Actual execution อยู่ [Verification](BE-005-VERIFICATION.md)

## 1. Executable preparation tests

| Group | Expected |
|---|---|
| Input required/type | null/blank username, empty password, control chars และ malformed/types JSON ถูก reject อย่างปลอดภัย |
| Credential preservation | ภาษาไทย/emoji/newline/leading-trailing spaces/combining characters ไม่ถูก normalize หรือ trim |
| Resource limits | boundary ผ่าน, over-limit characters/UTF-8 bytes ไม่ผ่าน; malformed Unicode ไม่ถูก replace |
| Secret handling | inbound credentials deserialize ได้แต่ JSON output เป็น `{}`, ToString ไม่เผยข้อมูล |
| DTO allowlist | userId/displayName เท่านั้น; role/userId/hash fields ที่ใส่ใน login request ถูก reject |
| Result invariants | success/current-user ไม่ยอมรับ empty stable ID, rejection/unavailable ไม่มี user, defaultไม่ใช่success |
| Runtime gate | Development/Production ไม่มี IAuthenticationBackend registration/Auth routes; ไม่ออก Cookie |
| Test seam | แม้ inject stub success เข้า test host ก็ไม่เปิด Login route ไม่เรียก stub หรือออก Cookie |

ใช้ synthetic strings ใน tests เท่านั้น ไม่อ่านหรือสร้าง Company credentials/hash
tests ของ invalid Unicode ต้องสร้าง lone surrogate ตอน runtime เพราะ attribute metadata/xUnit แทนที่ string ที่เสีย
POST unknown route อาจเป็น404หรือ405จาก routing/static-asset fallback ต้องยืนยันด้วย endpoint inventory และ no-cookie ไม่ใช้ status อย่างเดียว

## 2. Required future tests before Authentication can be enabled (Not Run)

| ID | Area | Cases / expected outcomes |
|---|---|---|
| AUTH-01 | Reviewed verifier | correct/wrong password vectors, salt/encoding/format compatibility, empty/null, Unicode, truncation/length behaviour |
| AUTH-02 | Account status | wrong/unknown/disabled/locked ทุกกรณี generic failure body/status; none creates session |
| AUTH-03 | Enumeration/timing | dummy verification/approved mitigation; measure repeated timings and bound work, not only compare messages |
| AUTH-04 | MySQL boundary | confirmed schema, parameterized values incl SQL payload, least necessary fields, read-only grants, no copied hashes |
| AUTH-05 | Dependency failure | unavailable/timeout safe503 or approved mapping, no stack/raw SQL/connection data; never fallback to mock |
| AUTH-06 | Cancellation | cancelled before/while DB/hash work propagates, no session after cancellation, no automatic retry |
| AUTH-07 | Request parser | null/array/malformed/wrong/duplicate/unknown fields, over body limit, content-type enforcement |
| AUTH-08 | Basic session | valid Login issues new approved auth ticket/cookie; /me uses server principal; cookie Secure/HttpOnly/SameSite/Path |
| AUTH-09 | Cookie challenge | anonymous/expired /me gives approved JSON401, no HTML redirect; no response caching |
| AUTH-10 | CSRF | Login/Logout and other relevant mutations reject missing/wrong token; token bootstrap/rotation agreed; cross-origin tests |
| AUTH-11 | Logout | clear cookie/session as approved, idempotency policy, no internal details, never GET mutation |
| AUTH-12 | Rate limit | per-identity/IP and global hash concurrency bounds,429+Retry-After, no unbounded username partitions, proxy-IP trust |
| AUTH-13 | Logging | canary password/hash/connection/raw exceptions must be absent from logs and safe responses; no body logging |
| AUTH-14 | Data Protection | persistent keys, ACL, restart/deployment behaviour, fail-safe missing/unreadable key configuration |
| AUTH-15 | Regression | existing Chat/SSE/UI tests and CI count/hygiene gates still pass; auth rollout changes reviewed explicitly |

## 3. Implementation sequence after approval

1. Freeze SEC-001 commit/decision references and confirmed input/API/status/identity contract; adjust this draft first
2. Evaluate necessary MySQL driver licensing, compatibility and dependency security before adding a package
3. Implement read-only repository with confirmed names + parameterized values + bounded timeout/cancellation; isolated DB tests
4. Implement reviewed verifier/adapter and account handling; no rehash/migration/Company schema writes
5. Configure approved basic cookie/CSRF/rate-limit/key persistence and safe error mapping without changing Chat validation globally
6. Implement Login/Logout/Me together with session tests; do not expose a credential-only Login endpoint without a session
7. Full unit/integration/security/regression verification; reviewer approval before enabling and Definition of Done

Advanced session revalidation/revoke-all/roles are BE-006, UI is FE-003, persistence is BE-007.
No production deployment, password migration, account-table edits or scope change is authorized by preparation work.
