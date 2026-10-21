# SEC-003 — คู่มือสคริปต์จัดการบัญชีสำหรับ Admin

สคริปต์ `bc-admin` (โค้ดอยู่ที่ `tools/BcAdmin`, ตัวเรียกคือ `scripts/security/bc-admin.ps1`) ใช้จัดการบัญชีผู้ใช้ BC-LocalAI ในตาราง `users`
(schema ของ SEC-001) โดยไม่ต้องแก้ MySQL ด้วยมือ ทุกคำสั่งหมุน `security_stamp` และ**ฐานข้อมูลเขียน audit เองด้วย trigger**
ใน transaction เดียวกับการเปลี่ยน

สถานะ: **ยังไม่ได้ใช้กับฐานจริง** ผ่านการทดสอบกับ fixture แยก (`sec003-mysql`) เท่านั้น ก่อนใช้กับฐานจริงต้องให้ DBA ตรวจและติดตั้งตามข้อ 3
(ดู [SEC-003-VERIFICATION.md](SEC-003-VERIFICATION.md))

## 1. คำสั่ง

ทุกคำสั่งต้องมี `--db-config <full path>` และ `--actor <ชื่อคุณ>` เลือกได้ว่าจะใส่ `--database <ชื่อฐาน>` (ถ้าไม่มี `database=` ในไฟล์)
ส่วน `--user` ใส่ได้ทั้ง id (UUID) หรือ username (ตัวพิมพ์เล็ก/ใหญ่ไม่มีผล เหมือนตอน login)
`--local-dev-database` ใช้ได้เฉพาะฐานพัฒนาบน loopback (ไม่ตรวจ certificate แต่ยังบังคับ TLS) เหมือน `Authentication:AllowLocalDevelopmentDatabase` ของแอป

| คำสั่ง | สิ่งที่เปลี่ยนในตาราง `users` | session ที่ login อยู่ |
|---|---|---|
| `create --username <u> --display-name <ชื่อ> [--role User\|Admin]` | เพิ่มบัญชีใหม่ (เปิดใช้งาน, stamp ใหม่) | — |
| `disable --user <u>` | `is_active = 0` + stamp ใหม่ | ถูกตัด |
| `enable --user <u>` | `is_active = 1` + stamp ใหม่ | (ไม่มี เพราะบัญชีถูกปิดอยู่) |
| `reset-password --user <u>` | `password_hash` ใหม่ + stamp ใหม่ | ถูกตัด, รหัสเก่าใช้ไม่ได้ |
| `set-role --user <u> --role User\|Admin` | `role` + stamp ใหม่ | ถูกตัด, login ใหม่ได้ role ใหม่ |
| `revoke-sessions --user <u>` | stamp ใหม่อย่างเดียว | ถูกตัดทุกอุปกรณ์ |
| `print-grants --writer-host <host> [--writer-user <name>]` | ไม่แตะฐาน พิมพ์ SQL ของบัญชีและ grant ให้ DBA | — |

"ถูกตัด" หมายถึง request ถัดไปได้ 401 และคำตอบที่กำลังสร้างอยู่หยุดภายใน 5 วินาที (กลไกของ BE-006)

ตัวอย่าง (PowerShell 7):

```powershell
pwsh -File scripts/security/bc-admin.ps1 create --db-config C:\BcAdmin\admin-writer.cnf --actor "สมชาย" --username somsri --display-name "สมศรี ใจดี"
pwsh -File scripts/security/bc-admin.ps1 disable --db-config C:\BcAdmin\admin-writer.cnf --actor "สมชาย" --user somsri
```

### รหัสผ่าน

Policy ที่ใช้ (เจ้าของโครงการเลือกตามข้อเสนอของ identity-policy review เมื่อ 2026-10-21; ข้อที่เหลือดูข้อ 5):

- อย่างน้อย **15 ตัวอักษร** นับเป็น code point (emoji 1 ตัว = 1) ตาม NIST SP 800-63B rev.4 §3.1.1.2, ไม่บังคับรูปแบบ (ตัวใหญ่/ตัวเลข/สัญลักษณ์)
- ไม่เกิน 1024 หน่วย UTF-16 และ 4096 byte UTF-8 (เพดานเดียวกับ login), ไม่ใช่ช่องว่างล้วน, ต้องเป็นข้อความ Unicode ที่ถูกต้อง
- username ที่ยาว 4 ตัวขึ้นไปห้ามอยู่ในรหัส (ไม่สนตัวพิมพ์เล็ก/ใหญ่) ใช้ทั้งตอน `create` และ `reset-password` แม้ใส่ `--user` เป็น id
- Admin ควรสุ่มรหัสเริ่มต้น (16 ตัวขึ้นไป) และส่งให้ผู้ใช้ทางช่องทางอื่น reset ทันทีเมื่อสงสัยว่ารั่ว (ไม่ต้องเปลี่ยนเป็นรอบ)
- พิมพ์ที่ prompt ที่ซ่อนตัวอักษร **2 ครั้ง** หรือส่งผ่าน pipe ด้วย `--password-stdin` (อ่าน 1 บรรทัด) ถ้าใช้ `--password-stdin`
  ที่ console ตรงๆ สคริปต์ปฏิเสธ เพราะตัวอักษรจะแสดงบนจอ
- **ห้ามใส่รหัสผ่านเป็น argument** สคริปต์ปฏิเสธทุกตัวเลือกที่ขึ้นต้นด้วย `--password` (ยกเว้น `--password-stdin`)
- เก็บเป็น hash IdentityV3 (HMAC-SHA512, 220,000 รอบ, salt 16 byte) ซึ่งเป็นรูปแบบเดียวที่ `IdentityPasswordVerifier` ของแอปรับ

### ผลลัพธ์และ exit code

สำเร็จจะพิมพ์ 1 บรรทัด เช่น `OK disable user=<id> sessions-revoked=yes audit=<เลข audit>` ไม่มีการพิมพ์รหัสผ่าน, hash, stamp หรือ connection string

| exit | ความหมาย |
|---|---|
| 0 | สำเร็จ |
| 2 | คำสั่ง, ตัวเลือก หรือรหัสผ่านไม่ถูกต้อง ไม่มีอะไรเปลี่ยน |
| 3 | ไม่พบบัญชี ไม่มีอะไรเปลี่ยน |
| 4 | username ซ้ำ ไม่มีอะไรเปลี่ยน |
| 5 | ไฟล์ credential, สิทธิ์, TLS หรือฐานข้อมูลมีปัญหา (รวมถึงไม่มี trigger ของ audit) ไม่มีอะไรเปลี่ยน |
| 6 | การเชื่อมต่อหลุดระหว่าง commit: **ไม่รู้ว่าบันทึกแล้วหรือยัง** ตรวจบัญชีและ audit ก่อนรันซ้ำ |
| 1 | error ที่ไม่คาดไว้ (แสดงแค่ชนิดของ exception) ตรวจบัญชีและ audit ก่อนรันซ้ำ |

## 2. Audit

ตาราง `admin_audit_log` เขียนโดย **trigger ของฐานข้อมูล** (`database/sec003/admin-audit.sql`) ทุกครั้งที่ `users` ถูกเพิ่มแถว หรือ
`password_hash`, `is_active`, `role`, `security_stamp` เปลี่ยน **ไม่ว่าจะเปลี่ยนด้วยสคริปต์, `mysql` client หรือ DBA** แถว audit อยู่ใน
transaction เดียวกับการเปลี่ยน ถ้าเขียน audit ไม่ได้ (ค่ายาวเกิน, ตารางมีปัญหา) การเปลี่ยนจะล้มทั้งหมด และสคริปต์ปฏิเสธการทำงาน
ถ้าไม่พบแถว audit (เช่นฐานยังไม่มี trigger)

| คอลัมน์ | ค่า | ใครเป็นคนกำหนด |
|---|---|---|
| `occurred_at_utc` | เวลา UTC | MySQL |
| `actor` | ค่าจาก `--actor` ถ้าแก้ไม่ผ่านสคริปต์จะเป็น `(direct SQL)` | ผู้ใช้ (ปลอมได้) |
| `os_user` | `DOMAIN\user` ของ Windows ที่รันสคริปต์ ถ้าไม่ผ่านสคริปต์จะเป็น `(unknown)` | ผู้ใช้ (ปลอมได้) |
| `db_user` | `USER()` = บัญชี MySQL และเครื่องที่ต่อเข้ามาจริง | MySQL (writer ปลอมไม่ได้) |
| `action` | `create` / `disable` / `enable` / `reset_password` / `set_role` / `revoke_sessions` | trigger ตัดสินจากข้อมูลเก่าเทียบใหม่ |
| `target_user_id` | id ของบัญชี | trigger |
| `detail` | `role=<ค่า>` สำหรับ `create` และ `set_role` | trigger |

- 1 แถวต่อการเปลี่ยน 1 ประเภท ถ้า SQL เดียวเปลี่ยนหลายอย่าง (เช่นปิดบัญชีพร้อมเปลี่ยนรหัส) จะได้หลายแถว ถ้าเปลี่ยนแค่ stamp จะได้ `revoke_sessions`
  ดังนั้น `enable` บัญชีที่เปิดอยู่แล้วจะบันทึกเป็น `revoke_sessions` (สิ่งที่เปลี่ยนจริง)
- การบันทึกการ login ผิดของ BE-005 (`access_failed_count`, `lockout_end_utc`) ไม่ใช่การแก้บัญชี จึงไม่มีแถว audit
- ไม่มีคอลัมน์สำหรับรหัสผ่าน, hash หรือ stamp บัญชี writer **ไม่มีสิทธิ์ใดๆ บน `admin_audit_log`** (อ่าน, เขียน, แก้, ลบไม่ได้)
- ดู audit ด้วยบัญชี DBA หรือบัญชีอ่านที่แยกต่างหาก:

```sql
SELECT occurred_at_utc, actor, os_user, db_user, action, target_user_id, detail
FROM admin_audit_log WHERE target_user_id = '<id>' ORDER BY id;
```

คำสั่งที่ล้มเหลว (ไม่พบบัญชี, username ซ้ำ, ไม่มีสิทธิ์) ไม่ได้เปลี่ยนอะไร จึงไม่มีแถว audit

## 3. ติดตั้งครั้งแรก (DBA, หลังได้รับอนุมัติ)

ต้องใช้ MySQL **8.0.29 ขึ้นไป** (แนะนำ 8.4 LTS) และตาราง `users` ตาม SEC-001 ต้องมีอยู่ในฐานแล้ว

1. Backup ฐานก่อน (runbook ฉบับเต็มของ DBA อยู่ในรายงาน DBA review; ดูข้อ 5)
2. สร้างตาราง audit และ trigger ด้วย `mysql` client (ไฟล์ใช้ `DELIMITER`):
   `mysql --database=<ฐาน> -e "source database/sec003/admin-audit.sql"`
   - เพิ่ม 1 ตาราง (InnoDB, utf8mb4) และ 2 trigger ไม่แก้และไม่ลบข้อมูลเดิม รันซ้ำได้ (`IF NOT EXISTS`)
   - บัญชีที่รันจะเป็น `DEFINER` ของ trigger และต้องมีสิทธิ์ INSERT บน `admin_audit_log` ตลอด (แนะนำบัญชีเฉพาะที่ lock ไว้)
   - ไฟล์ตั้ง `sql_mode` แบบ strict ก่อนสร้าง trigger (trigger ใช้ mode ตอนสร้าง) ถ้าเปิด binary log อาจต้องตั้ง `log_bin_trust_function_creators`
     ตามนโยบาย DBA
3. สร้างบัญชี writer ด้วย secret ที่สุ่มใหม่ แล้วรัน `database/sec003/admin-writer-grants.sql` (แทน `<admin-host>` ด้วย IP คงที่หรือ netmask
   ของเครื่อง Admin, **ห้าม `%`**) หรือใช้ผลจาก `print-grants --writer-host <host>` ซึ่งตรงกันเสมอ (มี unit test ตรวจ)

   | คำสั่ง | เหตุผล |
   |---|---|
   | `ALTER USER ... REQUIRE SSL WITH MAX_USER_CONNECTIONS 4 FAILED_LOGIN_ATTEMPTS 5 PASSWORD_LOCK_TIME 1` | บังคับ TLS, จำกัดการเชื่อมต่อ, ล็อกบัญชีเมื่อใส่รหัส MySQL ผิด |
   | `SELECT (id, normalized_username) ON users` | ใช้ใน `WHERE` และอ่าน username สำหรับกติการหัสผ่าน **ไม่มี** `password_hash`, `security_stamp`, `is_active`, `role` |
   | `INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users` | `create` |
   | `UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users` | คำสั่งที่เหลือ (ไม่มี `username`, `access_failed_count`, `lockout_end_utc`) |

   ไม่มี `DELETE`, ไม่มีสิทธิ์บน `admin_audit_log` หรือตารางอื่น (รวมถึงตารางแชตของ BE-007), ไม่มี `ON *.*`, ไม่มี `WITH GRANT OPTION`
4. ห้ามใช้บัญชี `bc_auth_reader` (ของ login) หรือ `bc_auth_attempt_writer` (ของ lockout) สคริปต์ปฏิเสธบัญชีเหล่านี้ รวมถึง `root`
   และ `bc_sec001_admin` ของ fixture ส่วนแอปก็ไม่ยอมใช้บัญชีที่ชื่อมีคำว่า `admin` อยู่แล้ว ([AuthenticationOptions.cs](../../BC-LocalAI/Configuration/AuthenticationOptions.cs))

แนะนำให้แต่ละ Admin มีบัญชี MySQL ของตัวเอง (grant ชุดเดียวกัน ใช้ `--writer-user`) แล้ว `db_user` จะระบุตัวคนได้โดยไม่ต้องพึ่ง `--actor`

## 4. ไฟล์ credential

ไฟล์รูปแบบ MySQL option file เก็บ**นอก repo** (เช่น `C:\BcAdmin\admin-writer.cnf`):

```ini
[client]
user=bc_admin_writer
password=<secret>
host=<db host>
port=3306
database=<ฐาน BC-LocalAI>
ssl-ca=C:\BcAdmin\mysql-ca.pem
```

- สคริปต์ปฏิเสธไฟล์ที่บัญชีอื่นนอกจากตัวคุณ, SYSTEM หรือ Administrators อ่านได้ ตั้งสิทธิ์ด้วย:
  `icacls C:\BcAdmin\admin-writer.cnf /inheritance:r /grant:r "%USERNAME%:F"`
- ต้องมี `ssl-ca` เสมอ (ตรวจ certificate และชื่อ host แบบ `VerifyFull`) ยกเว้นฐานพัฒนาบน loopback ที่ใส่ `--local-dev-database`
- ไม่รับ credential ผ่าน argument หรือ environment variable

## 5. สิ่งที่สคริปต์ไม่ทำ และความเสี่ยงที่เหลือ

- ไม่อ่านแชตหรือข้อมูลอื่นของผู้ใช้ บัญชี writer ไม่มีสิทธิ์ตารางอื่นนอกจาก `users` (ทดสอบแล้วด้วยตารางจำลองแชต)
- ใครได้ไฟล์ credential ของ writer ไป**แก้บัญชีได้** (สร้าง Admin, reset รหัส) แต่**ทุกการแก้มี audit** พร้อม `db_user` จริง
  `actor`/`os_user` ปลอมได้ ส่วน DBA หรือบัญชี definer ยังแก้หรือลบ audit ได้ การเก็บรักษาและส่งออก audit อยู่ใน OPS-002
- ไม่ลบบัญชี (ใช้ `disable`) และไม่ปลด lockout (หมดเองใน 15 นาที ตาม BE-005)
- ไม่กันการถอด Admin คนสุดท้าย ซึ่งไม่ทำให้กู้ระบบไม่ได้ เพราะสคริปต์ใช้ credential ของ MySQL ไม่ได้ใช้ role ในแอป
- ยังไม่มี (เป็น issue ถัดไปตาม policy review): รายการรหัสผ่านยอดนิยมแบบ offline, NFC normalization, เปลี่ยนรหัสเอง + บังคับเปลี่ยนตอน
  login ครั้งแรก, เพดาน 100 ครั้งที่ login ผิดติดกัน, MFA สำหรับ Admin
- ไม่มีหน้าจอหรือ API สำหรับ Admin ในแอป

## 6. Fixture สำหรับนักพัฒนา

```powershell
pwsh -File scripts/security/local-mysql.ps1 -Action Initialize -InstanceName sec003-mysql -Port 3318
pwsh -File scripts/security/local-mysql.ps1 -Action Verify -InstanceName sec003-mysql -Port 3318   # 16 checks
$env:SEC003_MYSQL_PRIVATE_DIRECTORY = "$PWD\.local\sec003-mysql\private"
dotnet test tests/SEC003.AdminTests
pwsh -File scripts/security/local-mysql.ps1 -Action Stop -InstanceName sec003-mysql -Port 3318
```

Fixture นี้มี reader, attempt writer ของ BE-005, admin writer, ตาราง audit พร้อม trigger (จาก `admin-audit.sql` ไฟล์เดียวกับที่ DBA ใช้)
และบัญชีสมมติ 3 บัญชีของ SEC-001 credential อยู่ใน `.local/sec003-mysql/private/` (ACL เฉพาะผู้ใช้ปัจจุบัน, ไม่อยู่ใน git)
fixture เดิม (`sec001-mysql-v2`, `be005-mysql`) ไม่เปลี่ยน
