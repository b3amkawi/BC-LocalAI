# SEC-001 — ผลตรวจสิ่งที่มี และการเปลี่ยนขอบเขตสำหรับพัฒนา

วันที่: 2026-10-20 · Issue: [SEC-001 #20](https://github.com/b3amkawi/BC-LocalAI/issues/20)

**สถานะ: Partial / Legacy review blocked / New local design proposed — ไม่ใช่ Security Review ที่อนุมัติแล้ว**

## 1. ข้อเท็จจริงและคำสั่งเพิ่มเติม

- ตรวจจากฐาน `origin/main` = `a983b800d2b9b536f229a736f2699c9099eeff64`
- branch `watzapon_SEC-001` มีเอกสารเตรียมงานเดิม; pull main เข้าแล้วที่ `c2b000b`
- `BC-LocalAI/BC-LocalAI.csproj`: ASP.NET Core `net10.0`, ไม่มี MySQL/Identity persistence package
- `BC-LocalAI/Program.cs`: มี `UseAuthorization()` แต่ไม่มี `AddAuthentication()`, `AddCookie()` หรือ `UseAuthentication()`
- `ChatController` และ `ChatStreamController` ยังไม่มี `[Authorize]`; ไม่ถือเป็นระบบพนักงานพร้อมใช้
- ตรวจรายการไฟล์ tracked และโค้ดใน `BC-LocalAI/` / `BC-LocalAI.Tests/`: ไม่พบ Source Login บริษัท, Custom Hash หรือ Company User DDL
- **ไม่พบใน repo ไม่ได้แปลว่าบริษัทไม่มีระบบเดิม** และไม่ได้รับอนุญาตให้ค้นข้อมูลลับที่อื่น
- เครื่องมี MySQL Community Server **8.0.46**, MySQL80 service และ Workbench อยู่แล้ว

ผู้ใช้สั่งในแชตว่า หากไม่มีข้อมูลเดิมให้สร้างเอง และอนุญาตให้ดำเนินการด้วย MySQL Windows โดยตรง
จึงเพิ่มเฉพาะ **ฐานบัญชีสมมติแยกสำหรับพัฒนา** และเอกสารออกแบบ ยังไม่เขียน Login API/Cookie/UI
ไม่เปลี่ยน Issue บน GitHub ไม่ปิด Issue และไม่อ้างว่า Leader อนุมัติการแทนระบบพนักงานจริง

## 2. ผลตรวจ Legacy ตาม Scope 4.1–4.3

| รายการ | ผลจากหลักฐาน | การดำเนินการ |
|---|---|---|
| Login request / user lookup / SQL / error handling | ไม่มี Source ให้ตรวจ | Blocked — ขอ Source ที่อนุญาตก่อน |
| Algorithm / salt / iterations / encoding / format / compare | ไม่ทราบ | ห้ามเดาหรือสร้างของใหม่แล้วอ้างว่าเข้ากันได้ |
| Empty/null password และ hash compatibility | ยังไม่ได้ทดสอบกับ Legacy | ขอ synthetic vectors จากเจ้าของระบบ |
| MySQL user fields / stable ID / roles / account status | ไม่มี Company DDL | Schema ใหม่ใน `database/sec001/schema.sql` เป็นคนละระบบ |
| Session / failed login / audit | ไม่มี implementation เดิม | ไม่สามารถยืนยันว่าปลอดภัยหรือมีช่องโหว่ |
| ส่วนเดิมที่ reuse ได้ / ไม่ควร reuse | ยังตัดสินไม่ได้ | ไม่คัดลอก Authentication Logic |

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

## 5. สิ่งที่รอการตัดสินใจ

1. Leader/เจ้าของระบบยืนยันว่าจะใช้บัญชี BC-LocalAI แยกจริง หรือรอเชื่อมบัญชีบริษัท และปรับ Issue #20/#21 ให้ตรงกัน
2. ระบุผู้อนุมัติ Security Review, ผู้รับผิดชอบสร้าง/ปิดบัญชี และ Password Reset (ไม่มี self-registration ในข้อเสนอนี้)
3. อนุมัติอายุ Session/lockout/rate limit, roles, ขอบเขต DB write และการจัดเก็บ audit/retention
4. หากยังใช้ Legacy ต้องส่ง Source, DDL, บัญชีทดสอบที่อนุญาต และ security requirements ผ่านช่องทางปลอดภัย

เอกสารต่อเนื่อง: [Architecture](authentication-architecture.md), [API Contract](authentication-api-contract.md),
[Risk assessment](authentication-risk-assessment.md), [Test plan / handoff](authentication-test-plan.md)
