# QA-002 Results and Verification Report (D3, D5)

ทดสอบเมื่อ 2026-10-21 (2 รอบเต็มที่สั่งแยกกัน; รอบแรก 256 กรณีก่อน FE-003/SEC-003 และรอบปัจจุบัน 300 กรณีหลัง merge `origin/main`) บน Windows 11, .NET SDK 10.0.402, Python 3.11.9 ไม่ใช้ GPU และไม่ใช้ Ollama จริง
ระบบที่ทดสอบ: `origin/main` ที่รวม SEC-001, BE-004, BE-005, BE-006, BE-007, FE-003, SEC-003 (เปิดประวัติสนทนาด้วย `ChatHistory:Enabled` เฉพาะ host ทดสอบ) และงาน AI-006 ตรวจจาก pipeline จริง (`Program`) ไม่ได้แก้โค้ดของ Backend/Frontend

- Expected ของ 300 กรณีเขียนไว้ก่อนรัน (256 กรณีแรกใน commit `0b0d23b`, 44 กรณีกลุ่ม F ใน commit `7d2d93c` ก่อนเขียนโค้ดทดสอบของกลุ่มนั้น)และ commit แยก ([qa-002-test-matrix.md](qa-002-test-matrix.md), [qa002-cases.json](../../tests/security/qa002-cases.json)) ไม่มีการแก้ Expected หลังเห็นผล
- Actual บันทึกแยกต่อกรณีต่อรอบใน [qa-002-evidence.json](qa-002-evidence.json) และตาราง Expected เทียบ Actual รายกรณีใน [qa-002-results-table.md](qa-002-results-table.md)
- ข้อบกพร่องอยู่ใน [qa-002-defects.md](qa-002-defects.md)

## สรุป

| ชุด | กรณี | ผ่าน | ไม่ผ่าน | สังเกต |
| --- | ---: | ---: | ---: | ---: |
| M role × endpoint | 76 | 75 | 1 | 0 |
| A authentication | 39 | 39 | 0 | 0 |
| S session | 23 | 23 | 0 | 0 |
| I authorization / IDOR | 15 | 15 | 0 | 0 |
| C CSRF | 46 | 46 | 0 | 0 |
| H security headers | 12 | 8 | 4 | 0 |
| L ความลับใน error/log | 12 | 10 | 2 | 0 |
| U ไม่ล็อกอินแล้วไม่ถึง Ollama | 9 | 9 | 0 | 0 |
| MS session สิ้นสุดระหว่างสตรีม | 14 | 13 | 1 | 0 |
| Q ผู้ใช้เดียวยิงมาก / คิว | 3 | 2 | 0 | 1 |
| O ค่าตั้งต้นและการปิดฟีเจอร์ | 7 | 6 | 0 | 1 |
| F FE-003 / SEC-003 | 44 | 40 | 3 | 1 |
| **รวม** | **300** | **286** | **11** | **3** |

600 การรันกรณี (300 × 2 รอบ) ผลของทุกกรณีเหมือนกันทั้งสองรอบ ชุดทดสอบ .NET ของ QA มี 187 test (บางตัวบันทึกหลายกรณี) ใช้เวลา ~41 วินาทีต่อรอบ

**ผลหลักตามเกณฑ์ยอมรับของ Issue**

| เกณฑ์ | ผล |
| --- | --- |
| `/api/chat/stream` ถูกปฏิเสธเมื่อไม่มี session, ส่ง CSRF ได้ถูกต้อง, การเพิกถอนระหว่างสตรีมจบอย่างปลอดภัย | **ผ่านเกือบทั้งหมด** ปฏิเสธ 401 JSON ก่อนเริ่ม SSE; `fetch` POST แบบเบราว์เซอร์อ่านสตรีมได้ทั้งแนบและไม่แนบ token; การสิ้นสุด session 6 วิธี (logout, revoke-all, ปิดบัญชี, เปลี่ยน role, เปลี่ยน stamp, ครบ 8 ชม.) และฐานข้อมูลผู้ใช้ล่ม จบด้วย `error AUTH_SESSION_ENDED` ครั้งเดียว ไม่มี `completed` ข้อความหลังเพิกถอนไม่ถึงผู้ใช้ และการเชื่อมต่อ Ollama ถูกปิด **ยกเว้น** ช่วงที่ Ollama เงียบ (QA2-D05) |
| test matrix ครอบคลุมทุก role และ endpoint | **ผ่าน** 76 กรณี: ไม่ล็อกอิน, User, Admin และ session ที่ logout / ปิดบัญชี / หมด idle / ครบ 8 ชม. / เปลี่ยน role |
| ไม่ล็อกอินเรียก endpoint ที่ป้องกันไม่ได้ และตอบ 401/403 ถูกต้อง | **ผ่าน** ทุก endpoint 401 `AUTH_REQUIRED` (User → endpoint Admin 403 `AUTH_FORBIDDEN`) |
| IDOR ทุก endpoint ที่รับ ID | **ผ่าน** อ่าน/แก้/ลบ/ดูข้อความ/ส่งแชตเข้าสนทนาของผู้อื่น ได้ 404 เหมือนสนทนาที่ไม่มีอยู่ทุกไบต์ ข้อมูลเจ้าของไม่เปลี่ยน; Admin ก็อ่านของ User ไม่ได้; id ผิดรูปแบบได้ 400 ไม่ใช่ 500; เจ้าของมาจาก session ไม่ใช่ body/query/header |
| session หมดอายุและถูกเพิกถอน รวมปิดบัญชีระหว่างล็อกอิน | **ผ่าน** idle 30 นาที (29:59 ใช้ได้ / 30:00 ไม่ได้), 8 ชม. ไม่ต่อด้วยการใช้งาน, ปิดบัญชีแล้วเปิดคืนก็ไม่กลับมา, role/stamp/ลบบัญชีมีผลคำขอถัดไป, ฐานข้อมูลล่ม fail closed |
| login ผิดไม่เผยว่าชื่อมีอยู่ + rate limiting | **ผ่าน** รหัสผิด/ไม่มีชื่อ/บัญชีปิด/บัญชีล็อก ได้ body และ header ชุดเดียวกัน; KDF ทำงานเท่ากัน (มัธยฐานเวลาชื่อที่ไม่มี/ชื่อที่มี 0.86 ถึง 1.18); 5/นาที/บัญชี, 30/นาที/IP; ล็อก 5 ครั้ง 15 นาที |
| คำขอที่เปลี่ยนสถานะมี CSRF protection | **ผ่าน** `/api/chat`, `/stream`, สร้าง/แก้/ลบสนทนา, logout: 36 รูปแบบข้ามเว็บถูกปฏิเสธ 403 `AUTH_CSRF_INVALID` โดยไม่มีผลข้างเคียง; `text/plain` และฟอร์มได้ 415 |
| error และ log ไม่มีรหัสผ่าน hash token หรือรายละเอียดภายใน | **ผ่านเกือบทั้งหมด** body ของ client สะอาดทุกกรณี; log สะอาดจากรหัสผ่าน/hash/stamp/session id/cookie/CSRF token/ข้อความคำถาม มี 2 ข้อบกพร่องระดับต่ำ/ข้อมูล: QA2-D03, QA2-D04 |
| ไม่ใช้บัญชีหรือข้อมูลจริง | **ผ่าน** บัญชีสังเคราะห์ รหัสผ่านสุ่มใหม่ทุกรอบ ไม่มีการเชื่อมต่อ MySQL หรือ Ollama จริง |
| ข้อบกพร่องด้านความปลอดภัยทั้งหมดแก้และทดสอบซ้ำ | **ยังไม่ครบ** 10 รายการรอเจ้าของ (D01-D10: ต่ำ 6, ข้อมูล 3, ปานกลาง 1 ที่เป็นข้อสังเกต) ไม่มีข้อสูง |

## ข้อบกพร่องที่พบ (สรุป)

| ID | ความรุนแรง | เจ้าของ | เรื่อง | กรณี |
| --- | --- | --- | --- | --- |
| QA2-D01 | ต่ำ | Backend | `/api/*` ส่วนใหญ่ไม่มี `X-Content-Type-Options: nosniff` | QA2-H-11 |
| QA2-D02 | ต่ำ | Backend/Frontend | หน้า HTML นอกจากหน้าแชต (Privacy) ฝัง iframe ได้ | QA2-H-10 |
| QA2-D03 | ต่ำ | Backend | ข้อความ exception ที่ไม่คาดคิดลง log ทั้งหมด (ความลับใน exception จะลง log) | QA2-L-07 |
| QA2-D04 | ข้อมูล | Backend/DevOps | log ของ HttpClient แสดง URL/พอร์ต Ollama ที่ Information | QA2-L-01 |
| QA2-D05 | ต่ำ | Backend | session ที่สิ้นสุดตอน Ollama เงียบ ไม่ตัดสตรีมจนกว่าชิ้นถัดไปมา | QA2-MS-stalled-model |
| QA2-D06 | ปานกลาง (ข้อสังเกต) | Backend (BE-008) | ไม่มีเพดานคำขอพร้อมกันต่อผู้ใช้ | QA2-Q-03 |
| QA2-D07 | ข้อมูล | Backend (BE-006 doc) | `GET /` ไม่ล็อกอินเป็น 302 `/login` แล้ว แต่ BE-006 matrix ยังเขียน 200 | QA2-M-home-anonymous, H-01, H-08 |
| QA2-D08 | ต่ำ | Frontend | ไฟล์ mock ของ preview ถูกเสิร์ฟใน Production | QA2-F-32 |
| QA2-D09 | ต่ำ | Backend | `/api/chat[/stream]` ที่มี conversationId ผ่าน HTTP ได้ 307 แทน 403 (เมื่อตั้งพอร์ต HTTPS) | QA2-F-12 |
| QA2-D10 | ข้อมูล | Backend | stream ตั้ง `Cache-Control: no-cache` ทับ `no-store` | QA2-F-21 |

รายละเอียด หลักฐาน และข้อเสนอแก้ดู [qa-002-defects.md](qa-002-defects.md)

## ผลของ FE-003 / SEC-003 (กลุ่ม F, 44 กรณี)

| หัวข้อ | ผล |
| --- | --- |
| `X-BC-Expected-User` | **ผ่าน** ตรงกัน → 200 + `X-BC-User`; ไม่ตรง/ซ้ำ/ตัวพิมพ์ต่าง → 401 `AUTH_SESSION_CHANGED` กับ chat, stream, conversations, `/api/auth/me` โดย Ollama 0 และ session ของผู้เรียกไม่ถูกเพิกถอน; เส้นทางสำรอง (`/API/CHAT`, `/api/chat/`, `/Api/Conversations/`, `/api/CHAT/STREAM/`, `/api/auth/ME`) ถูกครอบคลุม; ล็อกอินทับบน browser เดียวกัน → แท็บเก่าได้ 401; หลัง logout ได้ `AUTH_REQUIRED` ค่าว่างทดสอบไม่ได้ (QA2-N06) |
| CSRF token ของการเปลี่ยนประวัติ | **ผ่าน** สร้าง/เปลี่ยนชื่อ/ลบ/chat/stream ที่มี conversationId: ไม่มี token, token ซ้ำ, token ปลอม, token ของผู้ใช้อื่น, token ของผู้ไม่ล็อกอิน → 403 `AUTH_CSRF_INVALID` โดยไม่เขียนข้อมูลและ Ollama 0; alias เส้นทางไม่เลี่ยงได้; ไม่มี token + conversationId ที่ไม่มีอยู่ → 403 (ไม่ใช้ 404 เป็นช่องตรวจ id); token ถูกต้อง → สำเร็จทั้ง 5; chat/stream แบบเดิมที่ไม่มี conversationId ไม่ต้องใช้ token (ไม่เปลี่ยน) |
| HTTPS | `/api/conversations*` และคำขอที่มี `X-BC-Expected-User` ผ่าน HTTP → 403 `AUTH_HTTPS_REQUIRED` **ผ่าน**; `/api/chat[/stream]` ที่มี conversationId ผ่าน HTTP เมื่อตั้งพอร์ต HTTPS → 307 (D09) |
| หน้า | `/login`: 404 เมื่อปิด Authentication, 403 ผ่าน HTTP, 200 ผ่าน HTTPS พร้อม CSP/no-store/nosniff และไม่สะท้อน returnUrl, redirect ไป `/` เมื่อล็อกอินแล้ว **ผ่านทั้งหมด**; `/` ไม่ล็อกอิน → 302 `/login` (D07), ผ่าน HTTP → 403, บัญชีถูกปิด → 302, หน้าแอปไม่มี id/stamp/cookie/token **ผ่าน**; `/preview/login` ไม่เปิดใน 3 เงื่อนไขทดสอบ **ผ่าน**; ไฟล์ mock ถูกเสิร์ฟ (D08) |
| header | response ที่ล็อกอินเป็น `no-store` + `X-BC-User` ยกเว้น stream (D10); 401 ไม่มี `X-BC-User` **ผ่าน** |
| ผลต่อกรณีรอบแรก | harness ส่ง `X-CSRF-TOKEN` อัตโนมัติกับการเปลี่ยนประวัติ (เหมือนหน้าเว็บ) Expected เดิมไม่ถูกแก้ ที่ต่างมีเฉพาะ `GET /` ของผู้ไม่ล็อกอิน (D07) กรณีสตรีมเพิกถอน (`QA2-MS-*`) ผลเดิมทั้งหมด |

## วิธีที่ใช้ และเหตุผล

| หัวข้อ | รายละเอียด |
| --- | --- |
| ชุดทดสอบ | `BC-LocalAI.Tests/Qa002/*.cs` (ไฟล์ใหม่ทั้งหมด ไม่แก้ไฟล์เดิม) รันและรวมหลักฐานด้วย [run_qa002.py](../../tests/security/run_qa002.py) ใช้ `SecurityHost`/`FakeUserStore`/`InMemoryChatHistoryRepository`/`Sec001Fixtures` เดิมของโปรเจกต์ทดสอบ |
| ทำไมไม่ black-box ผ่านโปรเซสจริง | login และการตรวจบัญชีทุก request ต้องใช้ MySQL (ไม่มีเส้นทางที่ไม่ใช้ MySQL ใน Production code และห้ามเพิ่ม) จึงใช้ `WebApplicationFactory` ที่รัน pipeline จริงทั้งหมด (middleware, cookie, antiforgery, session store, policy, controller, `OllamaAIService`/`OllamaStreamingService` จริง) โดยแทนเฉพาะที่เก็บบัญชี |
| login | กรณีกลุ่ม A ใช้ `MySqlAuthenticationBackend`, `IdentityPasswordVerifier` (IdentityV3, 220,000 รอบ) และ `LoginAttemptLimiter` จริง กับฐานข้อมูลจำลองที่ทำตามกติกาของ SQL ใน `MySqlAuthenticationAttemptStore` (นับ, ล็อกเมื่อครบเกณฑ์, ต้อง stamp ตรง/บัญชีใช้งาน/ไม่ติดล็อก) กลุ่มอื่นใช้ backend จำลองแบบเบากว่า แต่ผ่านปลายทาง `/api/auth/login` จริงเสมอ (ไม่ใช้ `__test/signin`) |
| Ollama | `HttpMessageHandler` ที่นับคำขอทุกครั้งที่ถึง Ollama และสั่งพฤติกรรมได้ (ปกติ, 500 พร้อมข้อความภายใน, ปฏิเสธการเชื่อมต่อ, body เสีย, สตรีมที่คุมจังหวะทีละชิ้นด้วย gate, คิวแบบทำทีละงาน) ไม่มีการเรียกเครือข่ายจริง |
| เวลา | `ManualClock` (idle/absolute/lockout/rate-limit โดยไม่ต้องรอจริง) ยกเว้นกรณี `QA2-MS-stalled-model` ที่รอจริง 10 วินาที |
| หลักฐาน | JSON หนึ่งบรรทัดต่อกรณีต่อรอบ มีเฉพาะสถานะ รหัสข้อผิดพลาด จำนวนคำขอที่ถึง Ollama และข้อความสั้น ไม่มี cookie, token, รหัสผ่าน หรือ hash (ตรวจด้วย `tests/test_qa002_scripts.py`) |
| ความไวของชุดทดสอบ | ชุดนี้จับความผิดปกติได้จริง: กรณี O-02 แสดงว่าเมื่อปิด Authentication คำขอไม่ล็อกอินได้ 200 (ชุดเดียวกันนี้คือ 401 เมื่อเปิด) และ 5 กรณีของข้อบกพร่องที่พบล้มเหลวด้วยค่า Actual ที่บันทึกไว้ ตัวนับ Ollama ตรวจแล้วนับ 1 ต่อคำขอ (`QA2-U-08`) |

## การบันทึกการแก้ชุดทดสอบเอง

Expected ในแค็ตตาล็อกไม่ได้ถูกแก้หลังเห็นผล สิ่งที่แก้ระหว่างพัฒนาชุดทดสอบเป็นข้อผิดพลาดของโค้ดทดสอบ ก่อนมีผลที่นับ:

| เรื่อง | สาเหตุและการแก้ |
| --- | --- |
| กรณี A-10 ถึง A-22 ล้มเหลวตอนรันครั้งแรก | โค้ดทดสอบไม่ได้ส่งตัวนับ Ollama ให้ `Verify` (แค็ตตาล็อกกำหนด Ollama 0 ถูกต้อง) แก้โค้ดทดสอบให้ส่งตัวนับ |
| กรณี C-allow-same-origin ล้มเหลว | โค้ดทดสอบแปลงสถานะสำเร็จของ create (201) และ delete (204) ผิด แก้การแปลง |
| กรณี L-01 วัดจำนวน Ollama เป็น 2 | โค้ดทดสอบเรียก chat และ stream ก่อนอ่านตัวนับ แก้ให้อ่านตัวนับหลัง chat |
| กรณี L-05 ล้มเหลวตอน login | ที่เก็บบัญชีสำหรับทดสอบ log ยังไม่ผูกกับ host ตอน login แก้ลำดับการสร้าง |
| กรณี U-09 | คำขอ HTTP ได้ 200 ใน host ทดสอบเพราะไม่ได้ตั้งพอร์ต HTTPS ซึ่ง middleware redirect ต้องใช้ จึงทดสอบทั้งสองแบบ: ตั้งพอร์ต (Expected ตัดสิน) ได้ 307 ผ่าน, ไม่ตั้งพอร์ต ได้ 200 (บันทึกเป็น QA2-N02) |
| Q-01 | ปรับเวลาทำงานของ Ollama จำลองจาก 120 เป็น 300 มิลลิวินาทีเพื่อให้เกิดการรอคิวจริง |

## สิ่งที่ไม่ได้ทดสอบและข้อจำกัด

- **MySQL จริง** ไม่ได้ทดสอบในงานนี้ SQL ของ `MySqlAuthenticationAttemptStore` (นับ/ล็อกแบบ atomic, ผู้ใช้ concurrent), `MySqlUserSecurityStateSource`, `MySqlChatHistoryRepository` และสิทธิ์ขั้นต่ำของบัญชีฐานข้อมูล อยู่ใน `tests/BE005.MySqlTests` และ `tests/BE007.MySqlTests` ที่ต้องมี MySQL ส่วน lockout ในรายงานนี้ใช้ฐานข้อมูลจำลองที่ทำตามกติกาเดียวกัน
- **เบราว์เซอร์จริงและหน้า login (FE-003)** ไม่ได้ทดสอบ: พฤติกรรม cookie `__Host-`/`Secure`/`SameSite` ในเบราว์เซอร์จริง, การส่ง Origin/Sec-Fetch-Site จริง, `fetch` + `ReadableStream` ในหน้าเว็บ (ทดสอบเฉพาะการเรียกด้วย header แบบเบราว์เซอร์), ปุ่ม login/logout, การแสดงข้อความ 401/429 FE-003 ยังไม่ merge
- **reverse proxy / TLS จริง**: ไม่ทดสอบ (`X-Forwarded-*` ไม่ได้ใช้โดยเจตนา), HTTPS redirect ทดสอบด้วยการตั้งพอร์ตเท่านั้น, HSTS ตรวจใน Production กับชื่อโฮสต์ที่ไม่ใช่ localhost
- **หลายอินสแตนซ์ / restart**: session store อยู่ในหน่วยความจำ (ตัดสินใจไว้แล้ว) ไม่ทดสอบ restart และ rate limit ข้ามอินสแตนซ์
- **โหลดสูงและ DoS** นอกขอบเขต: ทดสอบการยิง 7 คำขอพร้อมกันและ login พร้อมกัน 8 คำขอเท่านั้น; ไม่ทดสอบ session store เต็ม (`MaxSessions`) นอกจากที่ BE-005 ทดสอบไว้แล้ว
- **Host header / AllowedHosts**: ค่า `AllowedHosts: *` ไม่ได้ทดสอบการปลอม Host
- **การเข้ารหัส/การหมุนคีย์ของ cookie (DataProtection)**: ใช้ key ring แบบชั่วคราวใน host ทดสอบ ไม่ทดสอบการเก็บคีย์ด้วย DPAPI/ACL จริง (อยู่ใน BE-005 ที่ต้องตรวจบนเครื่อง deploy)
- **ความลับในไฟล์ log จริง/ระบบรวม log**: ตรวจเฉพาะ log ที่แอปเขียนผ่าน `ILogger` ใน host ทดสอบ ไม่ตรวจ log ของเซิร์ฟเวอร์เว็บภายนอก
- **ผู้เชี่ยวชาญด้านความปลอดภัยรีวิว** และ **Leader อนุมัติ** (ข้อ 10 ของ Definition of Done) ยังรอ: รายงานนี้เป็นผลของ QA เท่านั้น
- **การทดสอบซ้ำหลังแก้** QA2-D01 ถึง D10 ยังรอเจ้าของ
- **`X-BC-Expected-User` ค่าว่าง** ทดสอบไม่ได้ผ่าน TestServer (header ไม่ถึงแอป) ตรวจจากโค้ดเท่านั้น (QA2-N06)
- **หน้า login/แชตในเบราว์เซอร์จริง** ทดสอบเฉพาะ HTML/header ที่เซิร์ฟเวอร์ส่ง ไม่ได้รัน JavaScript ของหน้า (`login.js`, `frontend-http.js`, `authenticated-chat.js`)
- นอกขอบเขต Issue: การแก้ข้อบกพร่อง, penetration test ภายนอก, ประวัติสนทนา (QA-003), streaming (QA-001)

## วิธีรันซ้ำ

```bash
dotnet build BC-LocalAI.slnx
python tests/security/run_qa002.py --runs 2 --output docs/qa/qa-002-evidence.json
python tests/security/make_qa002_docs.py results
```

ขั้นตอนมือและรายละเอียดอื่นอยู่ใน [tests/security/README.md](../../tests/security/README.md)
