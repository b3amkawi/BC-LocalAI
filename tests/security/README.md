# tests/security — QA-002 authentication and authorization scripts (D2)

| ไฟล์ | หน้าที่ |
| --- | --- |
| [qa002_cases.py](qa002_cases.py) | แค็ตตาล็อกกรณีทดสอบและ **Expected** (เขียนก่อนรัน) สร้าง [qa002-cases.json](qa002-cases.json) |
| [make_qa002_docs.py](make_qa002_docs.py) | สร้าง `docs/qa/qa-002-test-matrix.md` (D1) และ `docs/qa/qa-002-results-table.md` (Expected เทียบ Actual) |
| [run_qa002.py](run_qa002.py) | รัน `dotnet test` ของชุด `Qa002` N รอบ (แยกโปรเซส) แล้วรวมเป็น `docs/qa/qa-002-evidence.json` |
| [qa002-known-defects.json](qa002-known-defects.json) | กรณีที่ระบบยังไม่ตรง Expected และ ID ข้อบกพร่อง (ดู `docs/qa/qa-002-defects.md`) |
| `../../BC-LocalAI.Tests/Qa002/*.cs` | ชุดทดสอบ (`WebApplicationFactory`, Ollama จำลองที่นับคำขอ, บัญชีสังเคราะห์) |
| `../test_qa002_scripts.py` | ตรวจแค็ตตาล็อก ตัวรวมผล และหลักฐาน (ไม่ต้องใช้ .NET) |

## รันอัตโนมัติ

```bash
dotnet build BC-LocalAI.slnx
python tests/security/run_qa002.py --runs 2
python tests/security/make_qa002_docs.py results
```

- ใช้ Ollama จำลองเท่านั้น ไม่เรียกพอร์ต 11434 และไม่ใช้ GPU; รันทีละ collection (CPU พอประมาณ) ใช้เวลาราว 1 นาทีต่อรอบ
- บัญชีทั้งหมดสังเคราะห์และรหัสผ่านสุ่มใหม่ทุกครั้ง ห้ามใส่บัญชีหรือรหัสผ่านจริง
- ผลของแต่ละกรณีถูกตัดสินจาก Expected ใน `qa002-cases.json` เท่านั้น ห้ามแก้ Expected หลังเห็นผล ถ้าระบบไม่ตรงให้เพิ่ม ID ใน `qa002-known-defects.json` และบันทึกใน `docs/qa/qa-002-defects.md` (กรณียังคงบันทึกเป็น `FAIL` พร้อม ID ข้อบกพร่อง และชุดทดสอบยังเขียว เพื่อไม่ทำให้ CI ล้ม) เมื่อเจ้าของแก้แล้วให้ลบบรรทัดนั้นแล้วรันซ้ำ กรณีต้องเป็น `PASS` ทั้งสองรอบ
- รันเฉพาะส่วน: `dotnet test BC-LocalAI.Tests --filter "FullyQualifiedName~Qa002CsrfTests"` (ตั้ง `QA002_EVIDENCE=<ไฟล์>` เพื่อให้บันทึกหลักฐาน)

## ขั้นตอนที่ต้องทำมือ (เมื่อ FE-003 และ MySQL พร้อม)

ส่วนนี้อัตโนมัติไม่ได้ในงานนี้ ทำบนเครื่องพัฒนาด้วยบัญชีสังเคราะห์ (`tools/Sec001Fixtures`) เท่านั้น ห้ามใช้ระบบ login จริงของบริษัท

1. ตั้งค่า `Authentication:Enabled=true` กับ MySQL ทดสอบ (บัญชี reader/writer แยกกัน) และเปิด `ChatHistory:Enabled` ให้เมื่อทดสอบประวัติ
2. เปิดหน้าแชตในเบราว์เซอร์: login ผิด 6 ครั้งในหนึ่งนาที ต้องเห็นข้อความรอ (429) ไม่ใช่ข้อความที่บอกว่าชื่อมีอยู่หรือไม่
3. login สำเร็จ ตรวจใน DevTools ว่า cookie `__Host-BC.LocalAI` เป็น Secure, HttpOnly, SameSite=Lax และไม่มี Domain; ปิดเบราว์เซอร์แล้วเปิดใหม่ ต้อง login ใหม่ (session cookie)
4. ส่งคำถามที่ตอบยาว แล้ว logout จากอีกแท็บระหว่างสตรีม หน้าแชตต้องแสดงข้อผิดพลาด `AUTH_SESSION_ENDED` และหยุดรับข้อความ
5. ให้ผู้ดูแลปิดบัญชีที่กำลังสตรีมอยู่ (ฐานข้อมูล) สตรีมต้องหยุดภายในราว 5 วินาทีและคำขอถัดไปได้ 401
6. จากเว็บอื่นบนเครื่องเดียวกัน (เช่น หน้า HTML ชั่วคราวที่ `http://localhost:9999`) พยายามเรียก `fetch("https://<host>/api/chat", {method:"POST", credentials:"include", ...})` ต้องไม่ได้คำตอบ (ถูกบล็อกโดย CORS/SameSite/403)
7. ทำซ้ำข้อ 2 ถึง 5 หลังเปิดผ่าน reverse proxy จริง (ถ้ามี) ตรวจว่า SSE ไม่ถูก buffer และ HSTS/HTTPS redirect ทำงาน
8. บันทึกผลเป็นตัวหนังสือ (ไม่มี cookie, token หรือรหัสผ่านในภาพหน้าจอ) ต่อท้าย `docs/qa/qa-002-results.md`
