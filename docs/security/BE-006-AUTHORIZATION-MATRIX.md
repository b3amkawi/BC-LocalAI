# BE-006 — Authorization Matrix (D5)

Issue: #22 · Decisions: SEC-001 (2026-10-20, delegated to watzapon) — roles **User / Admin**, idle **30 นาที**, absolute **8 ชั่วโมง**,
**Admin ไม่มีสิทธิ์อ่านแชตของคนอื่น**

## Roles

| Role | ใครได้ | ทำอะไรได้ |
|---|---|---|
| User | พนักงานทั่วไป | ใช้ AI Chat (`/api/chat`, `/api/chat/stream`) และ (BE-007) ดู/จัดการประวัติแชต **ของตัวเอง** |
| Admin | ผู้ดูแลบัญชี | ทุกอย่างที่ User ทำได้ + จัดการบัญชี (สร้าง/ปิด/reset ผ่านสคริปต์; สคริปต์ทำใน BE-005/006 ภายหลัง) |

`Knowledge Manager` ใน Issue #22 เป็นตัวอย่าง ไม่ได้เลือกใน SEC-001 จึงยังไม่มี; จะเพิ่มเมื่อมี Knowledge Base (BE-011)
role ที่ไม่รู้จัก (เช่น `KnowledgeManager`, `admin` ตัวเล็ก) ถูกปฏิเสธทั้งตอน sign-in และทุก request

## Policies → Endpoints

| Endpoint | Policy | Anonymous | User | Admin | หมายเหตุ |
|---|---|---|---|---|---|
| `GET /` (หน้าแชต), static files | ไม่มี | ✅ | ✅ | ✅ | หน้าเว็บโหลดได้; FE-003 จะแสดงหน้า login เอง |
| `POST /api/chat` | `ChatUser` | **401** | ✅ | ✅ | session จบระหว่างรอคำตอบ → 401 `AUTH_SESSION_ENDED` |
| `POST /api/chat/stream` | `ChatUser` | **401** (JSON ก่อนเริ่ม SSE) | ✅ | ✅ | session จบกลาง stream → `event: error` `AUTH_SESSION_ENDED` แล้วหยุด |
| Admin endpoints (อนาคต) | `Admin` | **401** | **403** | ✅ | ไม่มี endpoint ใดให้ Admin อ่านแชตผู้อื่น |
| `/api/auth/login`, `/csrf` (BE-005) | `[AllowAnonymous]` | ✅ | ✅ | ✅ | BE-005 กำหนด |
| `/api/auth/me`, `/logout` (BE-005) | authenticated | 401 | ✅ | ✅ | BE-005 กำหนด |
| ประวัติแชต (BE-007) | `ChatUser` + ownership | 401 | เฉพาะของตัวเอง | เฉพาะของตัวเอง | role ไม่ให้สิทธิ์อ่านของคนอื่น |

**เมื่อ `Authentication:Enabled = false` (ค่าเริ่มต้นตอนนี้):** `ChatUser` เปิดให้ทุกคนเหมือนเดิม (หน้าแชตปัจจุบันยังไม่มี login)
แต่ `Admin` ยังต้อง sign-in เสมอ และ guard กัน cross-site ปิดอยู่

## ข้อผิดพลาด (JSON เดียวกับ API อื่น ไม่ redirect ไปหน้า HTML)

| HTTP | Code | เมื่อไร |
|---:|---|---|
| 401 | `AUTH_REQUIRED` | ไม่มี session / หมดอายุ / ถูกเพิกถอน / บัญชีถูกปิด / stamp หรือ role เปลี่ยน / ตรวจบัญชีไม่ได้ |
| 403 | `AUTH_FORBIDDEN` | sign-in แล้วแต่ role ไม่พอ |
| 403 | `AUTH_CSRF_INVALID` | request ไม่ปลอดภัย (POST/PUT/PATCH/DELETE) ไปที่ `/api` จากเว็บอื่น |

ไม่บอก client ว่าถูกปฏิเสธด้วยเหตุผลไหน เหตุผลอยู่ใน audit log ฝั่ง server เท่านั้น (event 5003)

## เมื่อไร session ใช้ไม่ได้ (ตรวจทุก request)

| เงื่อนไข | ผล |
|---|---|
| ไม่ได้ใช้งาน ≥ 30 นาที | ปฏิเสธ + เพิกถอน |
| sign-in มาแล้ว ≥ 8 ชั่วโมง (ใช้งานต่อเนื่องก็ไม่ต่อ) | ปฏิเสธ + เพิกถอน |
| sign-out / Admin เพิกถอนทุก session ของผู้ใช้ | ปฏิเสธ (cookie ที่ถูกคัดลอกไปก็ใช้ไม่ได้) |
| บัญชีถูกปิด, ถูกลบ | ปฏิเสธ + เพิกถอน (เปิดบัญชีคืนแล้ว session เก่าก็ไม่กลับมา) |
| บัญชีถูกล็อกจากรหัสผิด 5 ครั้ง (BE-005) | login ใหม่ถูกปฏิเสธจนครบ 15 นาที; session ที่ login อยู่แล้ว**ใช้ต่อได้** |
| security stamp เปลี่ยน (เปลี่ยนรหัส/สถานะ/สิทธิ์) | ปฏิเสธ + เพิกถอน |
| role เปลี่ยน (ทั้งขึ้นและลง) | ปฏิเสธ + เพิกถอน ต้อง sign-in ใหม่ |
| ฐานข้อมูลผู้ใช้ติดต่อไม่ได้ | ปฏิเสธ (fail closed) แต่ **ไม่**เพิกถอน: ฐานกลับมาแล้วใช้ต่อได้ |
