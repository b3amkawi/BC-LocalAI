# SEC-001 — Local MySQL Verification & Runbook

วันที่: 2026-10-20 (Asia/Bangkok) · branch `watzapon_SEC-001`
ฐาน main: `a983b800d2b9b536f229a736f2699c9099eeff64`, pull merge: `c2b000b`
อัปเดต main อีกครั้งหลัง FE-002 merge: `91916ea0daf71c22d2abb10e36f97088f3bea63a`, local pull merge `d22bd5b`
Implementation commit: `f0b54c1`; ผล regression สุดท้ายด้านล่างเป็นหลัง pull รอบใหม่นี้

**Local database/tooling tested. ขอบเขต: ใช้บัญชีของ BC-LocalAI บน MySQL ตามที่เจ้าของโปรเจกต์สั่ง (ไม่มีระบบ Legacy ให้ตรวจ); security sign-off ของ design ยังรออยู่**

## 1. สิ่งที่ทำจริง

- ใช้ MySQL Community Server **8.0.46** ที่ติดตั้งอยู่แล้ว ไม่ติดตั้ง/อัปเกรด MySQL, Docker หรือ WSL
- ไม่แก้ config/บัญชี/password ของ MySQL80 เดิมที่ port3306
- เปิด process MySQL ใหม่ bind **127.0.0.1:3307**, ปิด mysqlx, ปิด binary log สำหรับ local fixture
- ฐาน `bc_localai_sec001` มี users, UUID stable ID, unique normalized username, IdentityV3 hash, status/role/stamp/lockout fields
- บัญชีสมมติ 3 บัญชี: `sec001.active`, `sec001.disabled`, `sec001.locked` ทุกบัญชี role User
- `bc_auth_reader` อ่านได้เฉพาะ users ไม่มี UPDATE; `bc_sec001_admin` เป็นผู้ดูแลเฉพาะ schema นี้พร้อม SHUTDOWN สำหรับ lifecycle
- credentials/hash/bootstrap/data/log อยู่ `.local/sec001-mysql-v2/` ที่ Git-ignore และจำกัด Windows ACL ให้ identity ปัจจุบันกับ SYSTEM
- `BC-LocalAI/` ไม่มีการเปลี่ยนจาก main; ไม่มี Auth API/Cookie/หน้า Login ที่เปิดใช้งานใหม่

## 2. คำสั่งที่รันและผลจริง

รันจาก worktree SEC-001; ไม่มีรหัสผ่านอยู่ในคำสั่ง
ผลตารางแรกเป็นก่อนรวม FE-002; ผลล่าสุดอยู่ตารางถัดไป ไม่รวมจำนวนเทสต์ข้ามรอบเข้าด้วยกัน

| Command / check | Actual result |
|---|---|
| `dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --filter FullyQualifiedName~Sec001FixtureTests` | 5 passed, 0 failed |
| `dotnet restore BC-LocalAI.Tests/BC-LocalAI.Tests.csproj --force --no-cache` | exit0; rerun ดึง metadata สำเร็จ ไม่มี NU1900 ในผลรอบสุดท้าย |
| `dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-restore` | **421 passed, 0 failed, 0 skipped** |
| `node --test 'tests/ui/*.test.mjs'` | **23 passed, 0 failed** |
| `dotnet build tools/Sec001Fixtures/Sec001Fixtures.csproj -c Release` | 0 warnings, 0 errors |
| `pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Initialize` (fixed version, clean v2 datadir) | exit0, 3 fixtures created |
| `pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Verify` | **11/11 passed** |
| `Stop` → `Start` → `Verify` | exit0 ทุกขั้น; 11/11 passed หลัง restart, users ยัง3 |

ผลสุดท้ายหลังรวม `main` ที่มี FE-002 แล้ว:

| Command / check | Actual result |
|---|---|
| `dotnet build BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-restore` | **0 warnings, 0 errors** |
| `dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-build` | **433 passed, 0 failed, 0 skipped** |
| `node --test 'tests/ui/*.test.mjs'` | **61 passed, 0 failed, 0 skipped** |
| local MySQL `Verify` | **11/11 passed** อีกครั้งหลัง pull |
| `Initialize` ซ้ำ | rejected; checksum ของ local credentials ก่อน/หลังเท่าเดิม (ไม่เผยแพร่ checksum/secret) |
| `Start` เมื่อ instance ยังทำงาน | rejected; PID เดิมไม่ถูกเปลี่ยน |
| Windows ACL | protected ACL, 2 explicit principals, no inherited rules |
| Git ignore / tracked files | private credentials/data ignored; `git ls-files .local` ไม่มีไฟล์ |
| PowerShell parser / Markdown local links | parser ผ่าน, local links 14 รายการมีปลายทางจริง |
| `git diff --quiet origin/main -- BC-LocalAI` | exit0 หลังรวม FE-002: แอปไม่มีการแก้เพิ่มในงาน SEC-001 |
| MySQL80 เดิม | Running ที่3306; instance ใหม่127.0.0.1:3307 |

Verification อ่าน Hash จาก DB ผ่านบัญชี reader แล้วส่งผ่าน process memory/stdin ให้ framework verifier
ยืนยัน correct password **3/3** และ reject wrong password **3/3** โดยไม่พิมพ์ password/hash
นี่เป็น local credential verification ไม่ใช่การ Login ผ่าน HTTP และไม่ได้แทน Account Status enforcement tests

11 database checks: users count, disabled/locked counts, no Admin fixture, no plaintext-password column,
reader SELECT, unknown username, loopback binding, mysqlx off, reader UPDATE denied1142,
normalized username unique constraint denied1062, stored hash verification

## 3. ปัญหาที่พบและการแก้ (ไม่ปิดบังรอบที่ไม่ผ่าน)

1. Sandbox รอบแรกทำให้ VSTest ติดต่อ testhost ไม่ได้ และ NuGet metadata เกิด NU1900
   รันนอก sandbox ที่ได้รับอนุญาตแล้ว test ผ่าน; force restore ใหม่สำเร็จ ไม่ปิด vulnerability auditing
2. MySQL bootstrap รอบแรกสร้าง users/reader ได้ แต่ root@localhost ไม่ตรงกับ TCP127.0.0.1 ภายใต้ skip-name-resolve
   readiness จึง timeout แทนรายงานสำเร็จ แก้ด้วย local provisioning account ที่ระบุ host127.0.0.1 ชัดเจน
   ไม่เปลี่ยน root/บัญชีของบริการเดิม ทดลองแก้เฉพาะ process test แล้วทดสอบ Initialize ใหม่ตั้งแต่ต้นบน datadir v2
3. mysqladmin แจ้งรับคำสั่ง shutdown ก่อน InnoDB ปล่อย file handles ทำให้ขั้นตอนเก็บข้อมูลรอบแรกพบ file lock
   เพิ่มการตรวจ PID/datadir และ `Wait-Process` ก่อนรายงานว่า Stop เสร็จ
   การเตรียม backup รอบแรกมีไฟล์ซ้ำจึงหยุดโดยไม่ทับ ข้อมูล/secret รอบแรกยังอยู่ใน ignored `.local/`
   ไม่ถือ directory รอบแรกที่แยกไฟล์ระหว่างทางเป็น backup ที่ restore-tested; ฐานปัจจุบันคือ **v2** เท่านั้น
   ไม่ลบ/reset อัตโนมัติ ทดสอบ Stop→Start ของ v2 สำเร็จแล้ว

## 3b. รอบรีวิวโค้ด (Claude, 2026-10-20) — ข้อบกพร่องที่พบและแก้

ตรวจโค้ด/สคริปต์ทั้งหมดของ SEC-001 อีกรอบ พบ 3 จุดและแก้แล้ว:

| # | ปัญหา | ผลกระทบ | การแก้ |
|---|---|---|---|
| 1 | `private/bootstrap.sql` ค้างอยู่หลัง Initialize | เก็บรหัสผ่าน DB ทุกบัญชีเป็น plaintext ไว้บนดิสก์โดยไม่จำเป็น (ใช้แค่ครั้งแรกกับ `--init-file`) | `local-mysql.ps1` ลบทิ้งใน `finally` หลังการ start ครั้งแรก ไม่ว่าสำเร็จหรือล้มเหลว |
| 2 | `bc_sec001_admin` ใช้รหัสผ่านเดียวกับ `root` | ใครได้ `admin.cnf` ก็ได้รหัส root ด้วย ผิดหลัก least privilege | แต่ละบัญชีมี secret ของตัวเอง (`DatabaseSecrets`) และไม่เก็บ root secret เลย (root@localhost ต่อผ่าน TCP ไม่ได้อยู่แล้วภายใต้ skip-name-resolve; admin พอสำหรับทุกขั้น lifecycle; กู้ instance พัฒนา = Initialize ใหม่) |
| 3 | เทสต์ชื่อ "...NoAdmin" แต่ไม่ได้ตรวจ role | ชื่อเทสต์อ้างสิ่งที่ไม่ได้ทดสอบ | เพิ่มการตรวจว่า INSERT ไม่กำหนด role (ใช้ค่า default `User`) |

เพิ่มเติม: แยกการสร้าง bootstrap SQL ไปที่ `BootstrapScript` เพื่อให้ทดสอบได้ และเพิ่ม `-Port` (ค่าเดิม 3307) ให้รัน instance ที่สองข้างกันได้โดยไม่แตะ instance เดิม

เทสต์ใหม่ (`Sec001FixtureTests` จาก 5 เป็น 9): secret ของ root/admin/reader ต่างกันทั้งหมด, reader ได้แค่ `SELECT` บนตาราง users, ไม่มี `GRANT ALL ... ON *.*`,
รหัสผ่านผู้ใช้ไม่อยู่ใน bootstrap, ปฏิเสธ secret ซ้ำหรือ secret ที่ไม่ได้สร้างจาก generator, option file ใช้ 127.0.0.1/TCP/port ที่กำหนด
Mutation check: admin ใช้ secret ซ้ำ root, ยอมให้ secret ซ้ำ, reader ได้ทุกตาราง, admin ได้ `ALL ON *.*`, fixture ได้ role Admin → ทุกกรณีมีเทสต์ล้ม

ทดสอบจริงบน instance ใหม่ที่ port **3317** (instance เดิม 3307 ไม่ถูกแตะ):
`Initialize` → ไม่มี `bootstrap.sql` เหลือ, มีแค่ `admin.cnf`/`reader.cnf`/`test-accounts.json`, secret ต่างกัน → `Verify` **11/11** →
admin สร้าง user ไม่ได้ (1227) และอ่าน `mysql.user` ไม่ได้ (1142) → `Stop` → `Start` → `Verify` 11/11 → `Stop`; `Initialize` ซ้ำถูกปฏิเสธ

ผลรวมหลังแก้: `dotnet test` **437** passed (0 warnings), Node **61** passed, PowerShell parser ผ่าน, `BC-LocalAI/` ไม่ต่างจาก main
Python (PowerShell 7): ผ่าน 72/72 ใน 3 จาก 4 รอบเต็ม; 1 รอบมี `test_chat_e2e_script.test_timeout_fails_one_case_and_continues` ล้ม
(เทสต์ของ AI-003 ที่ตั้ง timeout 1 วินาทีกับ mock 160 คำขอ; SEC-001 ไม่ได้แตะไฟล์นี้) ทำซ้ำไม่ได้อีกใน 11 รอบเดี่ยวและ 2 รอบเต็ม
ไม่ได้ข้อความ error ของรอบที่ล้มจึงยังยืนยันสาเหตุไม่ได้ ไม่ได้แก้ค่า timeout ของเทสต์เพื่อกลบอาการ — แจ้งเจ้าของ AI-003

**instance เดิมที่ 3307 (สร้างก่อนแก้) ยังมี `bootstrap.sql` และ admin/root ใช้ secret เดียวกันอยู่** ควรลบ `private/bootstrap.sql` ของ instance นั้น
หรือสร้าง instance ใหม่ด้วยสคริปต์ที่แก้แล้ว (ต้องให้เจ้าของ instance ตัดสินใจ ผู้รีวิวไม่ได้แก้ไฟล์ในโฟลเดอร์นั้น)

## 4. วิธีใช้งาน

```powershell
pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Verify
pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Stop
pwsh -NoProfile -File scripts/security/local-mysql.ps1 -Action Start
```

`Initialize` ใช้ครั้งแรกบน path ที่ยังไม่มีเท่านั้น; ไม่ลบทับ datadir หรือ rotate secret เมื่อรันซ้ำ
script ใช้ executable จาก `C:\Program Files\MySQL\MySQL Server 8.0\bin` เปลี่ยนด้วย `-MySqlBin` ได้
ไม่มี Windows service/autostart ของ instance ใหม่ ต้อง Start หลัง reboot

MySQL Workbench: สร้าง connection ชื่อ **BC-LocalAI SEC-001 (local)**, TCP/IP, host127.0.0.1, port3307
ใช้ `bc_auth_reader` สำหรับดูข้อมูล หรือ `bc_sec001_admin` เฉพาะจัดการฐานพัฒนา
รหัสผ่านเก็บใน `private/reader.cnf` / `private/admin.cnf` ภายใน local instance directory (รหัส root ไม่ถูกเก็บ ดูข้อ 3b)
เปิดดูเฉพาะใน editor บนเครื่องเมื่อจำเป็น ห้ามวางในแชต/Issue/ภาพ/PR ห้ามใช้บัญชี admin เป็น app credential
รหัสผ่านผู้ใช้สมมติอยู่ `private/test-accounts.json` ไม่ใช่รหัสผ่าน DB และยังใช้ Login หน้าเว็บไม่ได้

## 5. ข้อจำกัด / approval

- ไม่มี Legacy Login/Company MySQL tests เพราะเจ้าของโปรเจกต์ยืนยันว่าไม่มีระบบ/ข้อมูลบริษัทให้ (N/A ตามการตัดสินใจ)
- Auth API/session/CSRF/authorization/rate limit/ownership ยัง Not Run: implementation อยู่ BE-005/006/007/FE-003
- ไม่ได้รัน Python/live Ollama ในรอบนี้ เพราะไม่ได้แก้ AI scripts หรือ application runtime
- ไม่ได้สแกน Git history หรือระบบ MySQL80 เดิมทั้งระบบ ไม่อ้างว่าไม่มี secret/ช่องโหว่ทั่ว repository
- Design/API/policy เป็นข้อเสนอ ยังรอ Leader/security owner ยืนยัน revised scope และ sign-off
- ไม่มี push, PR, GitHub checkbox/Issue edit, merge main หรือ production deployment ในงานนี้

ดู [AC mapping และ Test Plan](authentication-test-plan.md), [Risk Assessment](authentication-risk-assessment.md)
