# BE-005 — Preparation while SEC-001 is being reviewed

วันที่ 2026-10-20 · [Issue #21](https://github.com/b3amkawi/BC-LocalAI/issues/21)
**Draft / preparation only. Not an enabled authentication feature and not approved SEC-001 output.**

## 1. Branch / baseline / scope

ทำงานใน `watzapon_BE-005` worktree `BC_BE005` แยกจากงานอื่น
Pull `main` **ก่อนแก้ไฟล์**: fast-forward จาก `96a5676` ถึง `665df8c909a5558764218ca5c2d09c57262a973c`
main ล่าสุดรวม FE-002, DEVOPS-001, QA-001, AI-007 แล้ว
SEC-001 และ BE-007 ไม่ถูก merge เข้ามา งานของแต่ละ branch ยังแยกกัน

ผู้ใช้อนุญาตให้เตรียมเฉพาะสิ่งที่ทำได้ก่อน SEC-001 เสร็จ จึงเพิ่ม DTO/interface/resource validation,
test-only backend stub, safety-boundary tests และ handoff/test plan
**ไม่เพิ่ม** MySQL connection/query/schema mapping, password/hash verification, Controller/route,
DI, session/cookie/CSRF/rate-limiter implementation หรือ production configuration
`Program.cs`, Chat/Ollama/UI และฐานข้อมูลทุกตัวไม่ถูกแก้

## 2. Components actually added

| Component | Source | Role / limitation |
|---|---|---|
| LoginRequest | [DTO](../../BC-LocalAI/Models/Authentication/LoginRequest.cs) | Inbound-only; reject unmapped JSON fields; deserialize credentials แต่ไม่ serialize กลับ; ToString/Debugger redact |
| Safe user/current-user DTO + internal result | [Models](../../BC-LocalAI/Models/Authentication/AuthenticationModels.cs) | userId/displayName เท่านั้น; success ต้องมี stable ID; default outcome เป็น rejection; result ไม่ใช่ session/token |
| IAuthenticationBackend | [Interface](../../BC-LocalAI/Services/Authentication/IAuthenticationBackend.cs) | Boundary สำหรับ approved adapter ภายหลัง; ไม่มี concrete production implementation |
| Request validator | [Validator](../../BC-LocalAI/Services/Authentication/AuthenticationRequestValidator.cs) | Pure resource guard, no normalization/hash/DB/session work |
| Mock backend | [Test-only stub](../../BC-LocalAI.Tests/Authentication/StubAuthenticationBackend.cs) | อยู่ใน test assembly เท่านั้น ไม่เป็น fallback ให้ production |

`LoginRequest` เป็น inbound DTO เท่านั้น ห้ามใช้ `PostAsJsonAsync(LoginRequest)` เป็น client request
เพราะ credentials ถูก ignore เมื่อ serialize (จะได้ `{}`) ใช้ frontend/raw JSON ตาม approved wire contract
System.Text.Json attributes และ ToString redaction **ไม่ป้องกัน logger ที่ destructure ด้วย reflection**
ข้อกำหนดยังเป็น **ห้าม log/destructure request/credentials หรือ raw exception** ไม่ใช่ให้พึ่ง redaction อย่างเดียว
string credentials ยังอยู่ใน process memory; ไม่อ้างว่า zeroize หรือป้องกัน memory dump ได้

## 3. Proposed request and safe responses (not final contract)

```json
{"username":"synthetic.user","password":"<provided-in-browser>"}
```

Login success projection ตามตัวอย่าง BE-005:

```json
{"userId":"<stable-id-from-server>","displayName":"ผู้ใช้สมมติ"}
```

Current user success:

```json
{"authenticated":true,"user":{"userId":"<stable-id-from-server>","displayName":"ผู้ใช้สมมติ"}}
```

ไม่มี PasswordHash/Salt/AccountRow/ConnectionString/Role ที่ client กำหนดเองใน DTO
ต้องเทียบผล SEC-001 ก่อนใช้จริง โดยเฉพาะชื่อ `userId` vs `id`, envelope, anonymous `/me`, cookie/CSRF/error codes
draft นี้ไม่ถือว่าเป็น contract ที่ตกลงแล้วสำหรับ FE-003

## 4. Validation boundaries (draft resource ceilings)

- username ไม่เป็น null/blank และไม่มี control chars; ค่าเดิมไม่ถูก trim/case-fold/normalize
- password ต้องไม่เป็น null/empty แต่ไม่ reject whitespace password ตามนโยบายที่ยังไม่ทราบ
- limits เสนอ username128 UTF-16 units, password1024 UTF-16 units /4096 UTF-8 bytes; ทดสอบ custom limits ได้
- reject ill-formed Unicode แทนการแทนที่ code points เงียบ ๆ; invalid configuration ต้อง fail
- ทุก validation failure เป็น safe `AUTH_INVALID_REQUEST` เดียวกัน ไม่ echo input
- นี่ไม่ใช่ password policy, SQL injection prevention, account-status validation หรือ login rate limiting
- ยังไม่มี HTTP body-size/duplicate-JSON-key guard เพราะไม่มี endpoint ให้ configure; ต้องทำก่อนเปิดใช้งานจริง

## 5. Result contract for future implementation

`Success` → safe user only; ต้องสร้าง approved ASP.NET authenticated session ต่อ ไม่ return200จาก result อย่างเดียว
wrong/unknown/disabled/locked → `InvalidCredentials` เดียวกัน
DB/provider unavailable → `Unavailable` โดยไม่มี raw reason/hash ใน public result
caller cancellation ต้อง propagate โดยไม่ auto-retry credential check
ผล mock ของ interface ไม่ใช่หลักฐานว่ารหัสผ่านบริษัท/สถานะบัญชี/timeout ถูกตรวจจริง

## 6. SEC-001 handoff gate

ก่อนเขียน DAL/verifier หรือเปิด route ต้องได้รับ:

1. Approved Authentication Architecture / Security Review พร้อมผู้อนุมัติและ commit/evidence reference
2. Schema/lookup/collation/canonical username/stable ID/status mapping ที่ตรวจจริง ไม่เดาจาก fixture
3. Hash algorithm/format/salt/encoding/compare และ authorized synthetic vectors รวม wrong/empty/Unicode/long password
4. Company read-only grant + authorized test environment + TLS/CA/connection-secret delivery (ไม่ส่ง secrets ในแชต/Git)
5. Approved API/cookie/CSRF/session lifetime/Data Protection key persistence และ Login rate-limit policy
6. ถ้าเปลี่ยนเป็นบัญชี BC-LocalAI แยก ต้องยืนยัน revised Issue scope แทนการอ้างว่า compatible กับ Legacy

ต้องระบุ behaviour ของ legacy password whitespace/normalization/truncation ก่อนเอา resource ceilings ไปใช้งาน
BE-006 รับช่วง advanced revocation/revalidation/roles; BE-005 ยังต้องมี basic session และ CSRF ที่สมบูรณ์ก่อนเปิด Login
ไม่มี silent fallback ไป mock/local users เมื่อ Company MySQL ล่ม

หลักฐาน: [Verification](BE-005-VERIFICATION.md) · [Test plan / next implementation steps](BE-005-TEST-PLAN.md)
