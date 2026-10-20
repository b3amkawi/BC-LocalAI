# SEC-001 — ผลตรวจสิ่งที่มี และการเปลี่ยนขอบเขตสำหรับพัฒนา

วันที่: 2026-10-20 · Issue: [SEC-001 #20](https://github.com/b3amkawi/BC-LocalAI/issues/20)

**สถานะ: ขอบเขตชัดเจนแล้ว — ไม่มีระบบ Legacy ให้ตรวจ; BC-LocalAI ใช้บัญชีของตัวเองบน MySQL · Security design ยังรอผู้อนุมัติ sign-off**

> **การตัดสินใจของเจ้าของโปรเจกต์ (2026-10-20, ส่งต่อผ่าน watzapon):** ให้ใช้ **MySQL** และ **ไม่มีข้อมูล/ระบบ Login ของบริษัทให้** จึงสร้างระบบบัญชีของ BC-LocalAI เอง
> ส่วนที่ Issue #20 ขอให้ตรวจ Source/Hash/Schema ของบริษัทจึงเป็น **N/A (ไม่ใช้ตามการตัดสินใจ)** ไม่ใช่ "รอข้อมูล"
> หากวันหนึ่งบริษัทส่งระบบเดิมมาให้เชื่อม ให้เปิดงานใหม่ตามข้อ 4 และ [SEC-001-PREPARATION.md](SEC-001-PREPARATION.md)

## 1. ข้อเท็จจริงและคำสั่งเพิ่มเติม

- ตรวจจากฐาน `origin/main` = `a983b800d2b9b536f229a736f2699c9099eeff64`
- branch `watzapon_SEC-001` มีเอกสารเตรียมงานเดิม; pull main เข้าแล้วที่ `c2b000b`
- `BC-LocalAI/BC-LocalAI.csproj`: ASP.NET Core `net10.0`, ไม่มี MySQL/Identity persistence package
- `BC-LocalAI/Program.cs`: มี `UseAuthorization()` แต่ไม่มี `AddAuthentication()`, `AddCookie()` หรือ `UseAuthentication()`
- `ChatController` และ `ChatStreamController` ยังไม่มี `[Authorize]`; ไม่ถือเป็นระบบพนักงานพร้อมใช้
- ตรวจรายการไฟล์ tracked และโค้ดใน `BC-LocalAI/` / `BC-LocalAI.Tests/`: ไม่พบ Source Login บริษัท, Custom Hash หรือ Company User DDL
- **ไม่พบใน repo ไม่ได้แปลว่าบริษัทไม่มีระบบเดิม** และไม่ได้รับอนุญาตให้ค้นข้อมูลลับที่อื่น
- เครื่องมี MySQL Community Server **8.0.46**, MySQL80 service และ Workbench อยู่แล้ว

เจ้าของโปรเจกต์สั่งให้ใช้ MySQL โดยไม่มีข้อมูลบริษัท จึงใช้ **ระบบบัญชีของ BC-LocalAI เอง** (schema `database/sec001/schema.sql`)
งานนี้ส่งมอบฐานพัฒนาแยก + บัญชีสมมติ + เอกสารออกแบบ ยังไม่เขียน Login API/Cookie/UI (อยู่ใน BE-005/006/FE-003)
ไม่แก้ข้อความ Issue บน GitHub; การ sign-off ด้าน security ของ design ยังต้องมีผู้อนุมัติ (ข้อ 5)

## 2. Scope 4.1–4.3 (ตรวจระบบเดิม): N/A ตามการตัดสินใจ — ตารางนี้เก็บไว้เป็นบันทึกว่าไม่มีอะไรให้ตรวจ

| รายการ | ผลจากหลักฐาน | สิ่งที่ใช้แทน |
|---|---|---|
| Login request / user lookup / SQL / error handling | ไม่มีระบบเดิม (N/A) | ออกแบบใหม่ใน [Architecture](authentication-architecture.md) ข้อ 2 และ [API Contract](authentication-api-contract.md); implement ใน BE-005 |
| Algorithm / salt / iterations / encoding / format / compare | ไม่มีระบบเดิม (N/A) | `PasswordHasher` IdentityV3 (ข้อ 3) ทดสอบแล้ว; ไม่มีการอ้างว่าเข้ากันได้กับระบบอื่น |
| Empty/null password และ hash compatibility | ไม่มีระบบเดิม (N/A) | BE-005 ต้องปฏิเสธ empty/null/oversize password (Test plan ข้อ 3) |
| MySQL user fields / stable ID / roles / account status | ไม่มี Company DDL (N/A) | Schema ของ BC-LocalAI `database/sec001/schema.sql` |
| Session / failed login / audit | ไม่มี implementation เดิม (N/A) | ออกแบบใน Architecture ข้อ 4; implement ใน BE-005/006 |
| ส่วนเดิมที่ reuse ได้ / ไม่ควร reuse | ไม่มีระบบเดิม (N/A) | ไม่มี Authentication Logic ของบริษัทถูกคัดลอกมา |

## 3. การตรวจ Hash ของข้อมูลใหม่ (ไม่ใช่ Legacy)

ตัวสร้าง fixture ใช้ `PasswordHasher<string>` ของ Microsoft, IdentityV3, **220,000 iterations**
ไม่เขียน KDF/compare เอง ไม่ใช้ SQL `MD5`/`SHA2` สำหรับรหัสผ่านพนักงาน
Salt/format/verification ใช้ framework; Tests ตรวจ header ว่า HMAC-SHA512 และ salt อย่างน้อย 16 bytes
พร้อมตรวจ password ภาษาไทย/emoji/newline, รหัสผิด และ salt ที่ต่างกัน

ข้อเสนอนี้อ้างอิง [Microsoft PasswordHasher options](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0)
และ [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
ค่า production ยังต้องวัดเวลา Hash บนเครื่องใช้งานจริงและผ่าน Security Review
ไม่มีการ trim, lowercase หรือ normalize **password**; username policy เป็นคนละเรื่อง
การปฏิเสธ empty/null/oversize password ต้องทำใน BE-005 ไม่ใช่อ้างว่า fixture tool มี Login Validation แล้ว

## 4. แนวทางถ้าพบ Weak Legacy Hash ภายหลัง

หยุดการนำไปใช้ production ระบุ algorithm/ผลกระทบจาก Source จริงและแจ้งเจ้าของระบบ
พิจารณาใช้ช่องทางยืนยันตัวตนกลาง หรือ migration ไป adaptive hash โดยเจ้าของระบบอนุมัติ
ห้าม rehash, reset password, เพิ่ม DB write grant หรือยอมรับ weak format เงียบ ๆ ภายใต้ SEC-001
ไม่มีหลักฐานว่าระบบบริษัทใช้ weak hash ในรอบนี้

## 5. การตัดสินใจ: ที่ได้แล้ว และที่ยังรอ

| # | เรื่อง | สถานะ |
|---|---|---|
| 1 | ใช้บัญชี BC-LocalAI แยกบน MySQL หรือเชื่อมบัญชีบริษัท | **ตัดสินแล้ว: ใช้บัญชีของ BC-LocalAI บน MySQL** (ไม่มีระบบบริษัท) |
| 2 | ใครสร้าง/ปิด/reset บัญชีพนักงาน และใครเป็นผู้ sign-off ด้าน security | **ตัดสินแล้ว:** ผู้มี role **Admin** สร้าง/ปิด/reset บัญชีผ่าน**สคริปต์** (ไม่มี self-registration; ตัวสคริปต์ทำใน BE-005/006) · ผู้ sign-off ด้าน security คือ **watzapon (เติ้ฬ)** |
| 3 | อายุ Session, roles | **ตัดสินแล้ว:** idle **30 นาที**, absolute **8 ชั่วโมง** (renewal ไม่เกิน absolute) · roles **User / Admin**; **Admin ไม่มีสิทธิ์อ่านแชตของคนอื่น** |
| 3b | จำนวนครั้งของ lockout/rate limit, audit/retention | ยังไม่กำหนดตัวเลข — BE-005/006 เสนอพร้อมเหตุผลและเทสต์ ให้ผู้ sign-off อนุมัติใน PR ของ BE-005 |
| 4 | ข้อมูลระบบ Legacy | **N/A** — ไม่มีให้ (เปิดงานใหม่ถ้ามีภายหลัง) |
| 5 | BE-005 ใช้ฐานไหน | **ตัดสินแล้ว:** MySQL ของ BC-LocalAI เอง; บัญชี **reader** (อ่านอย่างเดียว) สำหรับตรวจ login และบัญชี **writer แยก** ที่แก้ได้เฉพาะคอลัมน์ lockout/attempt |
| 6 | instance เดิมที่ port 3307 (สร้างก่อนแก้สคริปต์) | **ตัดสินแล้ว:** ลบ `private/bootstrap.sql` (ทำแล้ว ดู Verification ข้อ 3b) |

ผู้ตัดสินข้อ 2, 3, 5, 6: watzapon (เติ้ฬ) ตามที่เจ้าของโปรเจกต์มอบสิทธิ์ให้ตัดสิน, 2026-10-20.

ผลต่อ BE-005 (#21): Issue เขียนว่า "Company MySQL" — ให้หมายถึง **ฐาน MySQL ของ BC-LocalAI** (`bc_localai_sec001` / schema นี้)
และ verifier คือ `PasswordHasher` มาตรฐาน ไม่มี Legacy adapter

เอกสารต่อเนื่อง: [Architecture](authentication-architecture.md), [API Contract](authentication-api-contract.md),
[Risk assessment](authentication-risk-assessment.md), [Test plan / handoff](authentication-test-plan.md)
