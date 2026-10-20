# BE-007 — Migration / Rollback Plan (Draft)

วันที่ 2026-10-20 · **ยังไม่ได้ execute กับ MySQL ใด และยังไม่อนุมัติสำหรับ production**
Scope: [Issue #23](https://github.com/b3amkawi/BC-LocalAI/issues/23) ส่วนเตรียม Database Schema / Repository Contract

## 1. Preconditions / ownership

- ตัดสินใจ application-owned database/schema และ stable ID contract จาก BE-005/006 ก่อนใช้จริง
- ห้าม apply บน Company MySQL แบบ read-only หรือฐานบัญชี SEC-001 โดยอัตโนมัติ
- Draft target MySQL >=8.0.17 เพราะต้องใช้ `utf8mb4_0900_bin`/NO PAD และ enforced CHECK constraints
- Migration account แยกจาก runtime; runtime เสนอ SELECT/INSERT/UPDATE เฉพาะสองตาราง ไม่ให้ DDL/GRANT/FILE
- เลือก TLS/CA/secret handling, backup storage, retention owner และ migration journal/tool ผ่าน review ก่อนจริง
- บันทึก server/database identity, schema version, SHA-256 ของ migration files และผล backup/restore rehearsal โดยไม่เก็บ credentials

## 2. Apply rehearsal (future isolated test database only)

1. ต้องได้รับอนุญาต target ที่ชัดเจน ใช้ schema ใหม่สำหรับข้อมูลสมมติ ตรวจไม่มี Conversations/Messages เดิม
2. Review [up SQL](../../database/chat-history/001-initial.up.sql) และให้ approved migrator เลือก schema explicit
3. Execute พร้อม stop-on-error; ไม่ใช้ IF NOT EXISTS เพื่อซ่อน schema ที่ไม่ตรง
4. ตรวจ DDL/index/collation/check/FK จริงจาก information_schema และ exact ID (case/accent/trailing-space/Unicode)
5. รัน shared repository contract กับ DB adapter รวม ownership, paging ties, concurrent sequence allocation และ delete/update race
6. ตรวจ failed/cancelled/partial message ไม่กลายเป็น completed, unique key rejects collision และ UTF-8 byte ceiling
7. ปิด connection/process ใหม่แล้วอ่านข้อมูลเดิมเพื่อยืนยัน durability; ไม่ใช้ in-memory tests แทน persistence

**DDL มี implicit commit**: CREATE สองตารางไม่ได้เป็น transaction เดียวกัน ถ้าตารางแรกสร้างแล้วตารางที่สองล้ม
ให้บันทึก partial state, ตรวจข้อมูล/DDL ที่เหลือ และให้ operator อนุมัติ reconciliation ห้ามรายงานว่า ROLLBACK คืนทุกอย่างแล้ว
ไม่มี application startup migration หรือ automatic repair ใน branch นี้

## 3. Rollback rehearsal / recovery

- [down SQL](../../database/chat-history/001-initial.down.sql) ใช้เฉพาะ pre-go-live **empty** schema ที่ operator ตรวจ target/อนุมัติแล้ว
- ต้องตั้ง `@BE007_ALLOW_EMPTY_ROLLBACK = 1` ใน connection เดียวกัน และ guard จะ SIGNAL หากไม่มี opt-in หรือมีข้อมูลในตารางใดตารางหนึ่ง
- Drop Messages ก่อน Conversations เพราะ FK; ไม่ปิด FOREIGN_KEY_CHECKS
- guard เป็น temporary stored procedure จึงต้อง CREATE ROUTINE permission ของ migrator ไม่ใช่ของ runtime
- หาก guard SIGNAL ให้หยุดและตรวจ helper procedure ที่ค้างก่อน retry; ห้ามเปิด force/continue-on-error และห้าม bypass เพื่อให้ผ่าน
- หลังมีข้อมูลจริง ห้ามใช้ down script นี้ แม้มี backup; rollback แอปโดยปิด feature/คืน binary/config ที่ approved และคงข้อมูลไว้
- ถ้าต้องแก้ schema/restore/purge ข้อมูล ต้องมี approved runbook พร้อม backup ที่ restore-tested และระบุผลกระทบ/ช่วงข้อมูลที่จะสูญเสีย

## 4. What is verified now

มีเพียง **static DDL contract checks**: schema source ไม่มี ALTER/DROP/GRANT/USE, มี exact-owner collation,
indexes/constraints และ rollback approval/non-empty guard อยู่ในไฟล์
ไม่ได้ยืนยัน SQL syntax/runtime behaviour/permissions/rollback จริงจาก assertions เหล่านี้
การอนุมัติ schema, apply/rollback rehearsal, real DB integration และ backup restore ยัง Pending/Not Run
