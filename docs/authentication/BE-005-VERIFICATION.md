# BE-005 — Preparation Verification

Historical result before SEC-001 was merged. Current implementation/results: [BE-005 implementation verification](BE-005-IMPLEMENTATION-VERIFICATION.md).

วันที่ 2026-10-20 (Asia/Bangkok) · [Issue #21](https://github.com/b3amkawi/BC-LocalAI/issues/21)
Branch `watzapon_BE-005`, worktree `BC_BE005`
Baseline main: `665df8c909a5558764218ca5c2d09c57262a973c` (pull ก่อนแก้ Source)

**Preparation only. Login/Logout/Me, MySQL integration, Password Verification และ Session ยังไม่ถูก implement/enable**
SEC-001 ยังไม่มี approved handoff ใน main ที่ตรวจ จึงไม่เดา Schema/Hash หรือเปิด Authentication เพื่อข้าม review

## 1. What actually changed

- Inbound-only LoginRequest ที่ redact ToString/Debugger และไม่ serialize credentials กลับผ่าน System.Text.Json
- safe user/current-user DTO และ internal result ที่ default เป็น rejection / success ต้องมี stable ID
- `IAuthenticationBackend` draft seam ไม่มี production implementation/DI และ test-only stub อยู่ใน test assembly เท่านั้น
- pure resource validation รักษาข้อมูลเดิม ไม่ trim/normalize password หรือเดาวิธี lookup/hash
- Tests สำหรับ Unicode/byte/character boundaries, serialization, allowed DTO fields, result invariants และ host safety gate
- CI minimum .NET count เพิ่ม416→467 ตามจำนวนที่รันจริง ป้องกันข้ามชุดใหม่; ไม่แก้ workflow/ลดเกณฑ์ Node/Python
- ไม่เพิ่ม dependency, ไม่อ่าน/เขียน DB, ไม่ merge SEC-001/BE-007 และไม่แก้ Program/Chat/SSE/UI

## 2. Executed checks

| Command / check | Actual result |
|---|---|
| First `dotnet test ... --filter FullyQualifiedName~Authentication` | 34 passed /5 failed เพราะ test-input metadata และ routing expectation (อธิบายด้านล่าง) |
| Focused suite after correcting test assumptions | **39 passed, 0 failed, 0 skipped** |
| `dotnet build BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-restore` | **0 warnings, 0 errors** |
| Development `dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-build` | **467 passed, 0 failed, 0 skipped** |
| Production คำสั่งเดียวกัน + ASPNETCORE_ENVIRONMENT=Production | **467 passed, 0 failed, 0 skipped** |
| `node --test 'tests/ui/*.test.mjs'` | **61 passed, 0 failed** |
| `python -I -X utf8 -m unittest discover -s tests -p 'test_*.py'` | **141 tests, OK, exit 0** (185.481 seconds) |
| `python scripts/ci/scan_secrets.py` หลัง stage ไฟล์ใหม่ | **Clean: 287 files checked**; ตรวจ tracked/staged files ไม่ใช่ Git history |
| `python scripts/ci/check_doc_links.py` | **OK: 51 Markdown files checked**; local file links ไม่รวม external URLs/anchors |
| `git diff --cached --check` | **Passed** |
| เปรียบเทียบ Program/Controllers/Views/wwwroot/Ollama services/CI workflow กับ baseline main | **Unchanged** |

Python suite มี ResourceWarning จากไฟล์ CSV ที่ยังไม่ปิดใน `tests/test_benchmark_scripts.py` และ loopback socket ที่ยังไม่ปิด
แต่ suite จบด้วย OK/exit 0; ไม่แก้โค้ด benchmark/CI ที่อยู่นอกขอบเขตนี้ และไม่อ้างว่า Python รันโดยไม่มี warning
ไม่ได้รัน test-count gate กับ raw output logs แยกต่างหาก; baseline .NET ปรับตามจำนวนจาก full suite ที่รันจริงข้างต้น

ผลนี้ครอบคลุม mock/test-host ไม่ใช่ Company MySQL/Auth HTTP integration
runtime gate ตรวจ GET /me404 และ POST Login/Logout404หรือ405 (ไม่มี handler), endpoint inventory ไม่มี `api/auth` route,
ไม่มี IAuthenticationBackend registration, ไม่มี Set-Cookie แม้ inject test-only success backend (calls=0)

## 3. Failed first run: diagnosis / correction

1. สอง malformed Unicode cases ใส่ lone surrogate ใน InlineData string แต่ .NET attribute metadata/xUnit แทนที่เป็น replacement chars
   จึงไม่ได้ทดสอบ input เสียจริง เปลี่ยนเป็นสร้าง lone surrogate ตอน runtime; strict UTF-8 validator ไม่ถูกลดความเข้มงวด
2. สาม host boundary cases สมมติว่า POST unknown route ต้อง404 แต่ route/static-asset fallback ให้405
   ปรับคาดหวังเป็น404/405 พร้อมตรวจ endpoint inventory + no-cookie + stub calls=0 ไม่แก้ routing/app/security controls เพื่อให้ test ผ่าน

## 4. Mutation checks

สำเนาแยก `artifacts/be005-mutation/` (Git-ignore):

- เปลี่ยน Password JSON ignore ให้ serialize ออกได้: `InboundCredentialsDeserializeButNeverSerializeBack` fail
- กลับทิศ UTF-8 byte ceiling: `Utf8ByteCeilingIsNotConfusedWithCharacterCount` fail

รันสองกรณีในสำเนาที่มี mutation: **2 failed, exit1** (ไม่ใช่ compile error)
คืนโค้ดทั้งสองจุดแล้ว rebuild/rerun: **2 passed, exit0**
ข้อมูลใน test เป็น synthetic strings ไม่ใช่ password/hash ของ Company หรือ local runtime account

## 5. Deliverables / acceptance status

| BE-005 requirement | Status |
|---|---|
| SEC-001 reviewed/approved | Pending; ไม่มีการอ้าง sign-off |
| Company MySQL / read-only / parameterized queries / timeout | Not implemented / Not Run; ต้อง Schema และสิทธิ์จริง |
| Legacy adapter / hash/salt/encoding / account status | Interface proposal only; verification logic Not implemented |
| Login/Logout/Me APIs and basic session | Safe DTO proposals only; ไม่มี Controller/routes/cookie issuance |
| Secure cookie/HTTPS/CSRF/rate limit | Test plan/handoff only; ยังไม่ implement |
| User enumeration / timing / DB error mapping | Generic result boundary only; real behaviour/timings Not Run |
| No credential duplication/logging | ไม่เพิ่ม storage/log statements และทดสอบ DTO redaction; ไม่ใช่การ audit logging ทั้งระบบ |
| Unit tests | New39 + regression467 ผ่าน; ไม่ถือว่าเป็น real Authentication integration |
| Integration/security tests | มี preparation runtime gate; full auth/DB/session/CSRF tests Not Run |
| Documentation | [Preparation](BE-005-PREPARATION.md), [Test plan](BE-005-TEST-PLAN.md), report นี้ |
| Independent review / DoD | ยังไม่ผ่าน reviewer sign-off และยังไม่ปิด Issue #21 |

## 6. Limitations and activation gate

ห้ามเปลี่ยน Interface/Mock tests เป็นหลักฐานว่าบัญชีพนักงาน Login ได้แล้ว
ต้องตรวจ approved SEC-001 Source/Schema/Hash/API/Session/CSRF/Rate-limit/Key-storage decisions ก่อน DAL/verifier/controller จริง
resource limits/DTO shape/error code เป็น Draft ไม่ใช่ company password policy หรือ agreed FE-003 contract
ไม่รัน live Ollama/browserใหม่ เพราะไม่มีการเปลี่ยน Chat runtime และไม่ได้เปิดหน้า Login
ไม่มี production deployment, migration/password reset หรือ DB grants ในงานนี้
