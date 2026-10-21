# SEC-003 — คู่มือสคริปต์จัดการบัญชีสำหรับ Admin

สคริปต์ `bc-admin` (โค้ดอยู่ที่ `tools/BcAdmin`, ตัวเรียกคือ `scripts/security/bc-admin.ps1`) ใช้จัดการบัญชีผู้ใช้ BC-LocalAI ในตาราง `users`
(schema ของ SEC-001) โดยไม่ต้องแก้ MySQL ด้วยมือ ทุกคำสั่งเขียน audit และหมุน `security_stamp` ใน transaction เดียวกัน

สถานะ: **ยังไม่ได้ใช้กับฐานจริง** ผ่านการทดสอบกับ fixture แยก (`sec003-mysql`) เท่านั้น ต้องให้ผู้ดูแลระบบอนุมัติ password policy
และ grant ก่อน (ดู [SEC-003-VERIFICATION.md](SEC-003-VERIFICATION.md))

## 1. คำสั่ง

ทุกคำสั่งต้องมี `--db-config <full path>` และ `--actor <ชื่อคุณ>` เลือกได้ว่าจะใส่ `--database <ชื่อฐาน>` (ถ้าไม่มี `database=` ในไฟล์)
ส่วน `--user` ใส่ได้ทั้ง id (UUID) หรือ username (ตัวพิมพ์เล็ก/ใหญ่ไม่มีผล เหมือนตอน login)

| คำสั่ง | สิ่งที่เปลี่ยนในตาราง `users` | session ที่ login อยู่ |
|---|---|---|
| `create --username <u> --display-name <ชื่อ> [--role User\|Admin]` | เพิ่มบัญชีใหม่ (เปิดใช้งาน, stamp ใหม่) | — |
| `disable --user <u>` | `is_active = 0` + stamp ใหม่ | ถูกตัด |
| `enable --user <u>` | `is_active = 1` + stamp ใหม่ | (ไม่มี เพราะบัญชีถูกปิดอยู่) |
| `reset-password --user <u>` | `password_hash` ใหม่ + stamp ใหม่ | ถูกตัด, รหัสเก่าใช้ไม่ได้ |
| `set-role --user <u> --role User\|Admin` | `role` + stamp ใหม่ | ถูกตัด, login ใหม่ได้ role ใหม่ |
| `revoke-sessions --user <u>` | stamp ใหม่อย่างเดียว | ถูกตัดทุกอุปกรณ์ |
| `print-grants --writer-host <host> [--writer-user <name>]` | ไม่แตะฐาน พิมพ์ SQL ของ grant ให้ DBA | — |

"ถูกตัด" หมายถึง request ถัดไปได้ 401 และคำตอบที่กำลังสร้างอยู่หยุดภายใน 5 วินาที (กลไกของ BE-006)
คำสั่งทุกตัวยกเว้น `print-grants` หมุน `security_stamp` เสมอ

ตัวอย่าง (PowerShell 7):

```powershell
pwsh -File scripts/security/bc-admin.ps1 create --db-config C:\BcAdmin\admin-writer.cnf --actor "สมชาย" --username somsri --display-name "สมศรี ใจดี"
pwsh -File scripts/security/bc-admin.ps1 disable --db-config C:\BcAdmin\admin-writer.cnf --actor "สมชาย" --user somsri
```

### รหัสผ่าน

- พิมพ์ที่ prompt ที่ซ่อนตัวอักษร **2 ครั้ง** (`create` และ `reset-password`)
- ถ้าต้องส่งจากโปรแกรมอื่น ใช้ `--password-stdin` (อ่าน 1 บรรทัดจาก standard input)
- **ห้ามใส่รหัสผ่านเป็น argument** สคริปต์ปฏิเสธทุกตัวเลือกที่ขึ้นต้นด้วย `--password` (ยกเว้น `--password-stdin`) เพราะ argument จะไปอยู่ใน
  shell history และรายการ process
- กติกาชั่วคราว (รอนโยบายบริษัท): 12–1024 ตัวอักษร, ไม่เกิน 4096 byte UTF-8, ไม่ใช่ช่องว่างล้วน, ห้ามมี username อยู่ในรหัส
- เก็บเป็น hash IdentityV3 (HMAC-SHA512, 220,000 รอบ, salt 16 byte) ซึ่งเป็นรูปแบบเดียวที่ `IdentityPasswordVerifier` ของแอปรับ

### ผลลัพธ์และ exit code

สำเร็จจะพิมพ์ 1 บรรทัด เช่น `OK disable user=<id> sessions-revoked=yes audit=<เลข audit>` ไม่มีการพิมพ์รหัสผ่าน, hash, stamp หรือ connection string

| exit | ความหมาย |
|---|---|
| 0 | สำเร็จ |
| 2 | คำสั่งหรือตัวเลือกไม่ถูกต้อง (ไม่ได้แตะฐานข้อมูล) |
| 3 | ไม่พบบัญชี ไม่มีอะไรเปลี่ยน |
| 4 | username ซ้ำ ไม่มีอะไรเปลี่ยน |
| 5 | ไฟล์ credential, สิทธิ์ หรือฐานข้อมูลมีปัญหา ไม่มีอะไรเปลี่ยน |
| 1 | error ที่ไม่คาดไว้ (แสดงแค่ชนิดของ exception) ถ้าการเชื่อมต่อหลุดระหว่าง commit จะไม่รู้ว่าเปลี่ยนไปแล้วหรือยัง ให้ตรวจบัญชีและ audit ก่อนรันซ้ำ |

## 2. Audit

ทุกคำสั่งที่เปลี่ยนบัญชีเพิ่ม 1 แถวใน `admin_audit_log` **ใน transaction เดียวกับการเปลี่ยน** ถ้าเขียน audit ไม่ได้ การเปลี่ยนจะถูก rollback
(ทดสอบแล้วกับ `disable`, `reset-password`, `create`)

| คอลัมน์ | ค่า |
|---|---|
| `occurred_at_utc` | เวลาที่ฝั่ง MySQL (UTC) |
| `actor` | ค่าที่ผู้ใช้พิมพ์ใน `--actor` |
| `os_user` | `DOMAIN\user` ของ Windows ที่รันสคริปต์ |
| `db_user` | `CURRENT_USER()` ที่ MySQL บันทึกเอง (ผู้ใช้ปลอมไม่ได้) |
| `action` | `create` / `disable` / `enable` / `reset_password` / `set_role` / `revoke_sessions` |
| `target_user_id` | id ของบัญชีที่ถูกเปลี่ยน |
| `detail` | `role=<ค่า>` สำหรับ `create` และ `set_role` เท่านั้น |

ไม่มีคอลัมน์สำหรับรหัสผ่าน, hash หรือ stamp บัญชี writer **อ่าน, แก้ หรือลบ audit ไม่ได้** (มีแค่ INSERT) การดู audit ให้ DBA หรือบัญชีอ่าน
ที่แยกต่างหากทำ เช่น:

```sql
SELECT occurred_at_utc, actor, os_user, db_user, action, target_user_id, detail
FROM admin_audit_log WHERE target_user_id = '<id>' ORDER BY id;
```

คำสั่งที่ล้มเหลว (ไม่พบบัญชี, username ซ้ำ, ไม่มีสิทธิ์) ไม่ได้เปลี่ยนอะไร จึงไม่มีแถว audit ผู้ใช้เห็นเหตุผลที่หน้าจอเท่านั้น

## 3. ติดตั้งครั้งแรก (DBA, หลังได้รับอนุมัติ)

1. สร้างตาราง audit ในฐาน BC-LocalAI (เพิ่มตารางเดียว ไม่แก้และไม่ลบข้อมูลเดิม รันซ้ำได้):
   `mysql --database=<ฐาน> < database/sec003/admin-audit.sql`
2. สร้างบัญชี writer ด้วย secret ที่สุ่มใหม่ และบังคับ TLS:
   `CREATE USER 'bc_admin_writer'@'<host ของเครื่อง Admin>' IDENTIFIED BY '<secret>' REQUIRE SSL;`
3. ให้สิทธิ์ตามที่สคริปต์พิมพ์ (`print-grants --writer-host <host>`) แล้วรันในฐาน BC-LocalAI:

   | สิทธิ์ | เหตุผล |
   |---|---|
   | `SELECT (id, normalized_username, is_active, role) ON users` | MySQL ต้องการสิทธิ์อ่านคอลัมน์ที่ใช้ใน `WHERE` และใช้หา id จาก username **ไม่มี** `password_hash` และ `security_stamp` |
   | `INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users` | `create` |
   | `UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users` | คำสั่งที่เหลือ (ไม่มี `username`, `access_failed_count`, `lockout_end_utc`) |
   | `INSERT (actor, os_user, db_user, action, target_user_id, detail) ON admin_audit_log` | audit แบบเขียนเพิ่มได้อย่างเดียว |

   ไม่มี `DELETE`, ไม่มีสิทธิ์ตารางอื่น (รวมถึงตารางแชตของ BE-007), ไม่มี `ON *.*`, ไม่มี `WITH GRANT OPTION`
4. ห้ามใช้บัญชี `bc_auth_reader` (ของ login) หรือ `bc_auth_attempt_writer` (ของ lockout) สคริปต์ปฏิเสธบัญชีเหล่านี้ รวมถึง `root`
   และ `bc_sec001_admin` ของ fixture ส่วนแอปก็ไม่ยอมใช้บัญชีที่ชื่อมีคำว่า `admin` อยู่แล้ว ([AuthenticationOptions.cs](../../BC-LocalAI/Configuration/AuthenticationOptions.cs))

ถ้าแต่ละ Admin มีบัญชี MySQL ของตัวเอง (grant ชุดเดียวกัน ใช้ `--writer-user`) คอลัมน์ `db_user` จะระบุตัวคนได้โดยไม่ต้องพึ่ง `--actor`

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
- ฐานที่อยู่เครื่องอื่นต้องมี `ssl-ca` (ตรวจ certificate และชื่อ host แบบ `VerifyFull`) ถ้าเป็น loopback ใช้ TLS แบบ `Required` ได้
  (กติกาเดียวกับแอป)
- ไม่รับ credential ผ่าน argument หรือ environment variable

## 5. สิ่งที่สคริปต์ไม่ทำ

- ไม่อ่านแชตหรือข้อมูลอื่นของผู้ใช้ บัญชี writer ไม่มีสิทธิ์ตารางอื่นนอกจาก `users` และ `admin_audit_log` (ทดสอบแล้วด้วยตารางจำลองแชต)
- ไม่ลบบัญชี (ใช้ `disable`) และไม่ปลด lockout (หมดเองใน 15 นาที ตาม BE-005) เพราะ writer ไม่มีสิทธิ์ `DELETE` และ `UPDATE` บนคอลัมน์ lockout
- ไม่กันการถอด Admin คนสุดท้าย ซึ่งไม่ทำให้กู้ระบบไม่ได้ เพราะสคริปต์ใช้ credential ของ MySQL ไม่ได้ใช้ role ในแอป
- ไม่มีหน้าจอหรือ API สำหรับ Admin ในแอป

## 6. Fixture สำหรับนักพัฒนา

```powershell
pwsh -File scripts/security/local-mysql.ps1 -Action Initialize -InstanceName sec003-mysql -Port 3318
pwsh -File scripts/security/local-mysql.ps1 -Action Verify -InstanceName sec003-mysql -Port 3318   # 14 checks
$env:SEC003_MYSQL_PRIVATE_DIRECTORY = "$PWD\.local\sec003-mysql\private"
dotnet test tests/SEC003.AdminTests
pwsh -File scripts/security/local-mysql.ps1 -Action Stop -InstanceName sec003-mysql -Port 3318
```

Fixture นี้มี reader, attempt writer ของ BE-005, admin writer, ตาราง audit และบัญชีสมมติ 3 บัญชีของ SEC-001
credential อยู่ใน `.local/sec003-mysql/private/` (ACL เฉพาะผู้ใช้ปัจจุบัน, ไม่อยู่ใน git) fixture เดิม (`sec001-mysql-v2`, `be005-mysql`)
ไม่เปลี่ยน
