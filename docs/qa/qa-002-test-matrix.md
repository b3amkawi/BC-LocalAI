# QA-002 Authentication and Authorization Test Matrix (D1)

ตาราง role × endpoint และกรณีทดสอบ authentication/authorization ของทั้งระบบ **Expected ทุกกรณีเขียนไว้ก่อนรันและเก็บใน [qa002-cases.json](../../tests/security/qa002-cases.json)** (สร้างจาก [qa002_cases.py](../../tests/security/qa002_cases.py)) ไฟล์นี้สร้างจาก catalog เดียวกัน ผล Actual บันทึกแยกใน [qa-002-evidence.json](qa-002-evidence.json) และ [qa-002-results.md](qa-002-results.md) ไม่มีการปรับ Expected หลังเห็นผล: ถ้าระบบไม่ตรงกับ Expected จะบันทึกเป็นข้อบกพร่องใน [qa-002-defects.md](qa-002-defects.md)

## วิธีทดสอบและสภาพแวดล้อม

| หัวข้อ | รายละเอียด |
| --- | --- |
| วิธี | `WebApplicationFactory<Program>` เรียก pipeline จริงทั้งหมด (middleware, cookie, session, antiforgery, controller) ผ่าน HTTPS จำลอง ใน `BC-LocalAI.Tests/Qa002/` สั่งรันและรวมหลักฐานด้วย `tests/security/run_qa002.py` |
| ทำไมไม่ black-box ผ่านโปรเซสจริง | เส้นทาง login และตรวจบัญชีทุก request ใช้ MySQL เท่านั้น (ไม่มีทางเลือกที่ไม่ใช้ MySQL ใน Production code และห้ามเพิ่ม) จึงใช้ host ทดสอบแทนส่วนฐานข้อมูล: ที่เก็บบัญชีจำลอง (`FakeUserStore`) และตัวนับความพยายาม login จำลอง |
| Ollama | จำลองด้วย `HttpMessageHandler` ที่นับคำขอ (ตัวนับ Ollama) ใช้ `OllamaAIService` และ `OllamaStreamingService` จริงเพื่อให้ข้อความผิดพลาดเป็นของจริง ไม่ต่อ Ollama จริง (พอร์ต 11434) |
| บัญชี | สังเคราะห์ทั้งหมด (alice, bob, admin1 ฯลฯ) รหัสผ่านสุ่มใหม่ทุกครั้งที่รัน ไม่มีบัญชีหรือรหัสผ่านจริง |
| เวลา | นาฬิกาที่ปรับด้วยมือ (`ManualClock`) เพื่อทดสอบ idle 30 นาที / absolute 8 ชั่วโมง / lockout 15 นาที โดยไม่ต้องรอจริง |
| รอบ | ทุกกรณีรัน 2 รอบเต็ม (สั่งแยกกัน) ผลรวมในหลักฐาน |

สัญลักษณ์ role ในตาราง M: `anonymous` ไม่ล็อกอิน · `user` User · `admin` Admin · `logged-out` cookie ของ session ที่ออกจากระบบแล้ว · `disabled` บัญชีถูกปิดระหว่างล็อกอิน · `idle-expired` ไม่ใช้งาน 30 นาที · `absolute-expired` ครบ 8 ชั่วโมง · `role-changed` เปลี่ยน role ระหว่างล็อกอิน

## M. 3.1 Role x endpoint matrix (76 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-M-home-anonymous | GET / โดย ไม่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-home-user | GET / โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-home-admin | GET / โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-csrf-anonymous | GET /api/auth/csrf โดย ไม่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-csrf-user | GET /api/auth/csrf โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-csrf-admin | GET /api/auth/csrf โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-me-anonymous | GET /api/auth/me โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-me-user | GET /api/auth/me โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-me-admin | GET /api/auth/me โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-me-logged-out | GET /api/auth/me โดย cookie ของ session ที่ logout แล้ว | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-me-disabled | GET /api/auth/me โดย บัญชีถูกปิดระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-me-idle-expired | GET /api/auth/me โดย session ไม่ได้ใช้งานครบ 30 นาที | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-me-absolute-expired | GET /api/auth/me โดย session ครบ 8 ชั่วโมง | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-me-role-changed | GET /api/auth/me โดย role ถูกเปลี่ยนระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-logout-anonymous | POST /api/auth/logout โดย ไม่ล็อกอิน | อนุญาต: HTTP 204 | 204 | - | - | BE-006 matrix |
| QA2-M-logout-user | POST /api/auth/logout โดย User ที่ล็อกอิน | อนุญาต: HTTP 204 | 204 | - | - | BE-006 matrix |
| QA2-M-logout-admin | POST /api/auth/logout โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 204 | 204 | - | - | BE-006 matrix |
| QA2-M-chat-anonymous | POST /api/chat โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-chat-user | POST /api/chat โดย User ที่ล็อกอิน | อนุญาต: HTTP 200; fake Ollama รับ 1 คำขอ | 200 | - | 1 | BE-006 matrix |
| QA2-M-chat-admin | POST /api/chat โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200; fake Ollama รับ 1 คำขอ | 200 | - | 1 | BE-006 matrix |
| QA2-M-chat-logged-out | POST /api/chat โดย cookie ของ session ที่ logout แล้ว | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-chat-disabled | POST /api/chat โดย บัญชีถูกปิดระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-chat-idle-expired | POST /api/chat โดย session ไม่ได้ใช้งานครบ 30 นาที | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-chat-absolute-expired | POST /api/chat โดย session ครบ 8 ชั่วโมง | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-chat-role-changed | POST /api/chat โดย role ถูกเปลี่ยนระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-anonymous | POST /api/chat/stream โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-user | POST /api/chat/stream โดย User ที่ล็อกอิน | อนุญาต: HTTP 200; fake Ollama รับ 1 คำขอ | 200 | - | 1 | BE-006 matrix |
| QA2-M-stream-admin | POST /api/chat/stream โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200; fake Ollama รับ 1 คำขอ | 200 | - | 1 | BE-006 matrix |
| QA2-M-stream-logged-out | POST /api/chat/stream โดย cookie ของ session ที่ logout แล้ว | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-disabled | POST /api/chat/stream โดย บัญชีถูกปิดระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-idle-expired | POST /api/chat/stream โดย session ไม่ได้ใช้งานครบ 30 นาที | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-absolute-expired | POST /api/chat/stream โดย session ครบ 8 ชั่วโมง | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-stream-role-changed | POST /api/chat/stream โดย role ถูกเปลี่ยนระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED; fake Ollama รับ 0 คำขอ | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-M-conv-list-anonymous | GET /api/conversations โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-list-user | GET /api/conversations โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-list-admin | GET /api/conversations โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-list-logged-out | GET /api/conversations โดย cookie ของ session ที่ logout แล้ว | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-list-disabled | GET /api/conversations โดย บัญชีถูกปิดระหว่างล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-create-anonymous | POST /api/conversations โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-create-user | POST /api/conversations โดย User ที่ล็อกอิน | อนุญาต: HTTP 201 | 201 | - | - | BE-006 matrix |
| QA2-M-conv-create-admin | POST /api/conversations โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 201 | 201 | - | - | BE-006 matrix |
| QA2-M-conv-get-own-anonymous | GET /api/conversations/{own} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-get-own-user | GET /api/conversations/{own} โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-get-own-admin | GET /api/conversations/{own} โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-get-other-anonymous | GET /api/conversations/{other user's} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-get-other-user | GET /api/conversations/{other user's} โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-get-other-admin | GET /api/conversations/{other user's} โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-patch-own-anonymous | PATCH /api/conversations/{own} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-patch-own-user | PATCH /api/conversations/{own} โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-patch-own-admin | PATCH /api/conversations/{own} โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-patch-other-anonymous | PATCH /api/conversations/{other user's} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-patch-other-user | PATCH /api/conversations/{other user's} โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-patch-other-admin | PATCH /api/conversations/{other user's} โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-delete-own-anonymous | DELETE /api/conversations/{own} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-delete-own-user | DELETE /api/conversations/{own} โดย User ที่ล็อกอิน | อนุญาต: HTTP 204 | 204 | - | - | BE-006 matrix |
| QA2-M-conv-delete-own-admin | DELETE /api/conversations/{own} โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 204 | 204 | - | - | BE-006 matrix |
| QA2-M-conv-delete-other-anonymous | DELETE /api/conversations/{other user's} โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-delete-other-user | DELETE /api/conversations/{other user's} โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-delete-other-admin | DELETE /api/conversations/{other user's} โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-msgs-own-anonymous | GET /api/conversations/{own}/messages โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-conv-msgs-own-user | GET /api/conversations/{own}/messages โดย User ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-msgs-own-admin | GET /api/conversations/{own}/messages โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-conv-msgs-other-anonymous | GET /api/conversations/{other user's}/messages โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-msgs-other-user | GET /api/conversations/{other user's}/messages โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-conv-msgs-other-admin | GET /api/conversations/{other user's}/messages โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-chat-conv-other-user | POST /api/chat {conversationId: other user's} โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND; fake Ollama รับ 0 คำขอ | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-chat-conv-other-admin | POST /api/chat {conversationId: other user's} โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND; fake Ollama รับ 0 คำขอ | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-stream-conv-other-user | POST /api/chat/stream {conversationId: other user's} โดย User ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND; fake Ollama รับ 0 คำขอ | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-stream-conv-other-admin | POST /api/chat/stream {conversationId: other user's} โดย Admin ที่ล็อกอิน | ปฏิเสธ 404: HTTP 404 CONVERSATION_NOT_FOUND; fake Ollama รับ 0 คำขอ | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น) |
| QA2-M-admin-only-anonymous | GET /api/__test/admin (stand-in for a future Admin-policy endpoint) โดย ไม่ล็อกอิน | ปฏิเสธ 401: HTTP 401 AUTH_REQUIRED | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-M-admin-only-user | GET /api/__test/admin (stand-in for a future Admin-policy endpoint) โดย User ที่ล็อกอิน | ปฏิเสธ 403: HTTP 403 AUTH_FORBIDDEN | 403 | AUTH_FORBIDDEN | - | BE-006 matrix |
| QA2-M-admin-only-admin | GET /api/__test/admin (stand-in for a future Admin-policy endpoint) โดย Admin ที่ล็อกอิน | อนุญาต: HTTP 200 | 200 | - | - | BE-006 matrix |
| QA2-M-health-anonymous | GET /health (no health endpoint is mapped) โดย ไม่ล็อกอิน | ไม่มี endpoint (404) ไม่เผยรายละเอียดระบบ: HTTP 404 | 404 | - | - | BE-006 matrix |
| QA2-M-health-user | GET /health (no health endpoint is mapped) โดย User ที่ล็อกอิน | ไม่มี endpoint (404) ไม่เผยรายละเอียดระบบ: HTTP 404 | 404 | - | - | BE-006 matrix |
| QA2-M-health-admin | GET /health (no health endpoint is mapped) โดย Admin ที่ล็อกอิน | ไม่มี endpoint (404) ไม่เผยรายละเอียดระบบ: HTTP 404 | 404 | - | - | BE-006 matrix |
| QA2-M-chat-stream-401-is-json | POST /api/chat/stream โดยไม่ล็อกอิน ตอบ JSON ไม่ใช่ SSE | 401 AUTH_REQUIRED เป็น application/json ก่อนเริ่ม SSE ไม่มี event-stream | 401 | AUTH_REQUIRED | 0 | BE-006 matrix (Issue #22) |

## A. 3.2 Authentication (login, failure messages, rate limit, lockout, logout) (39 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-A-01-login-ok | login ถูกต้อง | 200; ตัว body มีเฉพาะ user{id,displayName,username,roles}; ไม่มีรหัสผ่าน/hash/stamp/session; Cache-Control no-store | 200 | - | - | BE-005 contract |
| QA2-A-02-wrong-password | รหัสผ่านผิด (ชื่อมีอยู่) | 401 AUTH_INVALID_CREDENTIALS | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract |
| QA2-A-03-unknown-user | ไม่มีชื่อผู้ใช้นี้ | 401 AUTH_INVALID_CREDENTIALS เหมือนรหัสผ่านผิดทุกไบต์ (body เท่ากัน) | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract |
| QA2-A-04-disabled | บัญชีถูกปิด (รหัสถูก) | 401 AUTH_INVALID_CREDENTIALS body เท่ากับกรณีรหัสผิด | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract |
| QA2-A-05-locked | บัญชีถูกล็อก (รหัสถูก) | 401 AUTH_INVALID_CREDENTIALS body เท่ากับกรณีรหัสผิด | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract |
| QA2-A-06-no-cookie-on-failure | login ล้มเหลวทุกแบบไม่ออก cookie session | ไม่มี Set-Cookie ของ __Host-BC.LocalAI ที่มีค่า (ยกเว้นการลบ) | - | - | - | BE-005 contract |
| QA2-A-07-no-echo | ข้อความผิดพลาดไม่สะท้อนชื่อผู้ใช้หรือรหัสผ่าน | body ของ 401 ไม่มีชื่อผู้ใช้/รหัสผ่านที่ส่งไป | - | - | - | BE-005 contract |
| QA2-A-08-same-headers | header ของ login ล้มเหลวเหมือนกันทุกสาเหตุ | ชุดชื่อ header และสถานะเท่ากัน (ไม่นับ Date) | - | - | - | BE-005 contract |
| QA2-A-09-same-work | ชื่อที่ไม่มีก็ทำงาน KDF เท่ากัน (เวลาไม่ต่างจนเดาชื่อได้) | เวลามัธยฐานของชื่อที่ไม่มี >= 50% ของรหัสผิดชื่อที่มี | - | - | - | BE-005 (KDF ทุกกรณี) |
| QA2-A-10-no-csrf-token | login ไม่มี CSRF token | 403 AUTH_CSRF_INVALID | 403 | AUTH_CSRF_INVALID | 0 | BE-005 contract |
| QA2-A-11-bad-csrf-token | login ด้วย token ปลอม | 403 AUTH_CSRF_INVALID | 403 | AUTH_CSRF_INVALID | 0 | BE-005 contract |
| QA2-A-12-old-csrf-token | login ซ้ำด้วย token เดิมหลัง login แล้ว | 403 AUTH_CSRF_INVALID | 403 | AUTH_CSRF_INVALID | 0 | BE-005 contract |
| QA2-A-13-cross-origin | login จาก Origin ต่างเว็บ | 403 AUTH_CSRF_INVALID | 403 | AUTH_CSRF_INVALID | 0 | BE-005 contract |
| QA2-A-14-cross-site-fetch-metadata | login ที่ Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID | 403 | AUTH_CSRF_INVALID | 0 | BE-005 contract |
| QA2-A-15-http | login ผ่าน HTTP (ไม่ใช่ HTTPS) | 403 AUTH_HTTPS_REQUIRED ไม่รับรหัสผ่านทางข้อความล้วน | 403 | AUTH_HTTPS_REQUIRED | 0 | BE-005 contract |
| QA2-A-16-bad-input-not-json | อินพุตผิดรูป: Content-Type ไม่ใช่ JSON | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-17-bad-input-extra-role | อินพุตผิดรูป: มีฟิลด์ role แถม (mass assignment) | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-18-bad-input-duplicate | อินพุตผิดรูป: ฟิลด์ซ้ำ | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-19-bad-input-wrong-type | อินพุตผิดรูป: ชนิดข้อมูลผิด | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-20-bad-input-sql | อินพุตผิดรูป: ชื่อผู้ใช้เป็น SQL | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-21-bad-input-empty | อินพุตผิดรูป: ช่องว่าง | 400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie | 400 | AUTH_INVALID_REQUEST | 0 | BE-005 contract |
| QA2-A-22-oversized | body ใหญ่เกิน 16 KB | 413 REQUEST_TOO_LARGE | 413 | REQUEST_TOO_LARGE | 0 | BE-005 contract |
| QA2-A-23-rate-limit-account | ยิงรหัสผิดชื่อเดียวเกิน 5 ครั้ง/นาที | ครั้งที่ 1-5 ได้ 401; ครั้งที่ 6 ได้ 429 AUTH_RATE_LIMITED พร้อม Retry-After | 429 | AUTH_RATE_LIMITED | - | BE-005 contract |
| QA2-A-24-rate-limit-reset | หลังครบ 1 นาที ยิงได้ใหม่ | คำขอถัดไปไม่ใช่ 429 (ได้ 401 ตามปกติ) | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract |
| QA2-A-25-rate-limit-same-body | 429 ของชื่อที่มีกับชื่อที่ไม่มี เหมือนกัน | body เท่ากัน ไม่เผยว่าชื่อมีอยู่ | 429 | AUTH_RATE_LIMITED | - | BE-005 contract |
| QA2-A-26-rate-limit-ip | ยิงหลายชื่อจาก IP เดียว เกิน 30 ครั้ง/นาที | ครั้งที่ 31 ได้ 429 AUTH_RATE_LIMITED | 429 | AUTH_RATE_LIMITED | - | BE-005 contract |
| QA2-A-27-lockout | รหัสผิด 5 ครั้ง (ห่างกันเกินกรอบอัตรา) แล้วใส่รหัสถูก | ครั้งที่ 6 ที่รหัสถูก ได้ 401 AUTH_INVALID_CREDENTIALS เหมือนรหัสผิด (บัญชีถูกล็อก 15 นาที) | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 contract (ตัวแทน SQL แบบจำลอง) |
| QA2-A-28-lockout-expires | ล็อกครบ 15 นาที | login ด้วยรหัสถูกสำเร็จ 200 | 200 | - | - | BE-005 contract |
| QA2-A-29-lockout-keeps-session | บัญชีถูกล็อกจากรหัสผิด ขณะเจ้าของล็อกอินอยู่ | เจ้าของยังใช้ /api/auth/me ได้ 200 (ไม่ถูกเตะออกด้วยการเดารหัส) | 200 | - | - | BE-006 matrix |
| QA2-A-30-success-resets | รหัสผิด 4 ครั้ง ถูก 1 ครั้ง ผิดอีก 4 ครั้ง | ไม่ล็อก: ล็อกอินด้วยรหัสถูกหลังจากนั้นสำเร็จ 200 | 200 | - | - | BE-005 contract (ตัวแทน SQL แบบจำลอง) |
| QA2-A-31-username-case | ชื่อผู้ใช้ตัวพิมพ์ต่างกัน | ใช้ได้ (normalized) 200 | 200 | - | - | SEC-001 |
| QA2-A-32-password-exact | รหัสผ่านมีช่องว่างท้ายเพิ่ม | 401: รหัสต้องตรงทุกอักขระ ไม่ถูกตัดช่องว่าง | 401 | AUTH_INVALID_CREDENTIALS | - | BE-005 (preserve exact password) |
| QA2-A-33-db-down | ฐานข้อมูลผู้ใช้ล่มตอน login | 503 AUTH_UNAVAILABLE; ไม่มี cookie; body ไม่มีรายละเอียดภายใน | 503 | AUTH_UNAVAILABLE | - | BE-005 contract |
| QA2-A-34-logout | logout (มี CSRF token) | 204; ลบ cookie; ใช้ cookie เดิมซ้ำได้ 401 ทั้ง /me และ /api/chat (Ollama 0) | 204 | - | 0 | BE-006 matrix |
| QA2-A-35-logout-twice | logout ซ้ำ | 204 ทั้งสองครั้ง (idempotent) | 204 | - | - | BE-005 contract |
| QA2-A-36-logout-no-csrf | logout ไม่มี CSRF token | 403 AUTH_CSRF_INVALID และ session ยังไม่ถูกเพิกถอน | 403 | AUTH_CSRF_INVALID | - | BE-005 contract |
| QA2-A-37-me-shape | รูป response ของ /api/auth/me | มีเฉพาะ user{id,displayName,username,roles} | 200 | - | - | BE-005 contract |
| QA2-A-38-relogin | login เป็นอีกคนบน browser เดิม | session เดิมถูกเพิกถอน: cookie เก่าใช้ไม่ได้ 401 | 401 | AUTH_REQUIRED | - | BE-005 (re-login replaces session) |
| QA2-A-39-concurrent-login-cap | login พร้อมกันเกินเพดาน 2 | ไม่มีคำขอใดได้ 500; ทุกคำตอบเป็น 200/401/429 | - | - | - | BE-005 contract |

## S. 3.3 Session (expiry, revocation, cookie flags, fixation) (23 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-S-01-idle-29m | ไม่ใช้งาน 29 นาที 59 วินาที | session ยังใช้ได้ 200 | 200 | - | - | BE-006 matrix |
| QA2-S-02-idle-30m | ไม่ใช้งานครบ 30 นาที | 401 AUTH_REQUIRED และ session ถูกเพิกถอน (ใช้ต่อไม่ได้แม้ย้อนเวลา) | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-S-03-activity-extends-idle | ใช้งานทุก 20 นาทีตลอด 7 ชั่วโมง | ยังใช้ได้ 200 (idle ต่ออายุตามการใช้งาน) | 200 | - | - | BE-006 matrix |
| QA2-S-04-absolute-8h | ใช้งานต่อเนื่องจนครบ 8 ชั่วโมง | 401 AUTH_REQUIRED (การต่อเนื่องไม่ขยายอายุ) | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-S-05-absolute-7h59 | 7 ชั่วโมง 59 นาทีที่ใช้งานต่อเนื่อง | ยังใช้ได้ 200 | 200 | - | - | BE-006 matrix |
| QA2-S-06-disabled | ปิดบัญชีระหว่างล็อกอิน | คำขอถัดไป /api/chat ได้ 401 AUTH_REQUIRED Ollama 0 | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-S-07-reenabled-stays-dead | เปิดบัญชีคืนหลังถูกปิด | session เก่ายังใช้ไม่ได้ 401 (ต้อง login ใหม่) | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-08-role-up | เปลี่ยน User เป็น Admin ระหว่างล็อกอิน | session เก่า 401; login ใหม่เข้า endpoint Admin ได้ 200 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-09-role-down | เปลี่ยน Admin เป็น User ระหว่างล็อกอิน | endpoint Admin ได้ 401 ทันที (ไม่ใช้สิทธิ์ค้างเดิม) และไม่ได้ 200 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-10-stamp | เปลี่ยน security stamp (เปลี่ยนรหัส/สิทธิ์) | session เก่า 401 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-11-deleted | ลบบัญชีระหว่างล็อกอิน | 401 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-12-store-down | ฐานข้อมูลผู้ใช้ติดต่อไม่ได้ระหว่างใช้งาน | ปฏิเสธ 401 (fail closed ไม่ใช่ 500); ฐานกลับมาแล้ว session เดิมใช้ต่อได้ 200 | 401 | AUTH_REQUIRED | 0 | BE-006 matrix |
| QA2-S-13-revoke-all | เพิกถอนทุก session ของผู้ใช้ (2 เครื่อง) | ทั้งสอง cookie 401; ผู้ใช้อื่นไม่กระทบ 200 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-14-unknown-role | บัญชีมี role ที่ไม่รู้จัก (KnowledgeManager) | session ที่มีอยู่ 401 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-S-15-cookie-flags | flag ของ cookie session | ชื่อ __Host-BC.LocalAI; Secure; HttpOnly; SameSite=Lax; Path=/; ไม่มี Domain | - | - | - | BE-006 |
| QA2-S-16-csrf-cookie-flags | flag ของ cookie CSRF | ชื่อ __Host-BC.Csrf; Secure; HttpOnly; SameSite=Strict; Path=/; ไม่มี Domain | - | - | - | BE-005 |
| QA2-S-17-session-cookie | cookie ไม่คงอยู่เกินการปิดเบราว์เซอร์ | ไม่มี Expires/Max-Age (session cookie) | - | - | - | BE-006 (IsPersistent=false) |
| QA2-S-18-no-reissue | คำขอที่ล็อกอินแล้วไม่ออก cookie ใหม่/ต่ออายุ cookie | ไม่มี Set-Cookie ของ session ใน response ของ /api/auth/me | - | - | - | BE-006 (SlidingExpiration=false) |
| QA2-S-19-fixation-garbage | session fixation: ฝัง cookie session ปลอมก่อน login | login ออก cookie ใหม่ที่ไม่ใช่ค่าที่ฝัง; ค่าที่ฝังไม่เคยกลายเป็น session ที่ใช้ได้ (401) | 401 | AUTH_REQUIRED | - | BE-006 (session fixation) |
| QA2-S-20-fixation-valid-cookie | session fixation: attacker ถือ cookie ที่ valid ของตนแล้วเหยื่อล็อกอินบน browser เดียวกัน | cookie เดิมถูกเพิกถอนเมื่อมี login ใหม่ ใช้ต่อ 401 | 401 | AUTH_REQUIRED | - | BE-006 (session fixation) |
| QA2-S-21-tampered-cookie | cookie ถูกแก้ (ตัดท้าย / สลับตัวอักษร / ว่าง / ยาวมาก) | 401 ไม่ใช่ 500 ทุกรูปแบบ | 401 | AUTH_REQUIRED | 0 | BE-006 |
| QA2-S-22-cookie-opaque | ค่า cookie ไม่เปิดเผยข้อมูล | ค่า cookie ไม่มี user id, security stamp หรือ session id แบบข้อความล้วน | - | - | - | BE-006 (cookie ไม่มีความลับ) |
| QA2-S-23-two-users-isolated | สองผู้ใช้ล็อกอินพร้อมกัน | logout ของคนหนึ่งไม่กระทบอีกคน (200) | 200 | - | - | BE-006 matrix |

## I. 3.4 Authorization, IDOR, privilege escalation (15 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-I-01-list-own-only | รายการสนทนาแสดงเฉพาะของตัวเอง | Alice เห็นเฉพาะของ Alice; Bob เห็นเฉพาะของ Bob | 200 | - | - | BE-007 contract |
| QA2-I-02-other-same-as-missing | เปิดสนทนาของคนอื่น กับ id ที่ไม่มีอยู่จริง | 404 CONVERSATION_NOT_FOUND และ body เหมือนกันทุกไบต์ (ถามเดา id ไม่ได้) | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract |
| QA2-I-03-other-unchanged | แก้/ลบ/อ่านสนทนาของคนอื่นแล้วข้อมูลเจ้าของไม่เปลี่ยน | ชื่อ ข้อความ และตัวสนทนาของเจ้าของไม่เปลี่ยนหลังความพยายามของคนอื่น | - | - | - | BE-007 contract |
| QA2-I-04-chat-other-conv-no-write | ส่งแชตเข้าสนทนาของคนอื่น | 404 และไม่มีข้อความเพิ่มในสนทนาของเจ้าของ; Ollama 0 | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 contract |
| QA2-I-05-admin-no-read | Admin อ่าน/แก้/ลบ/ส่งแชตเข้าสนทนาของ User | 404 ทุกกรณี (Admin ไม่มีสิทธิ์อ่านแชตผู้อื่น) | 404 | CONVERSATION_NOT_FOUND | 0 | SEC-001 decision |
| QA2-I-06-invalid-ids | id รูปแบบผิด (ไม่ใช่ GUID, path traversal, SQL, GUID รูปแบบอื่น, ยาวมาก) | 400 INVALID_ID ทุกรูปแบบ ไม่ใช่ 500 | 400 | INVALID_ID | - | BE-007 contract |
| QA2-I-07-body-owner-ignored | สร้างสนทนาพร้อมฟิลด์ userId/ownerId ของคนอื่นใน body | สนทนาเป็นของผู้เรียก ไม่ใช่ของ id ที่ใส่มา | 201 | - | - | BE-007 contract (เจ้าของมาจาก session) |
| QA2-I-08-header-query-ignored | ใส่ ?userId= / X-User-Id / X-Forwarded-User ของคนอื่น | ถูกเมิน: ผู้เรียกเห็นเฉพาะของตัวเอง | 200 | - | - | BE-007 contract |
| QA2-I-09-cursor-cross-user | ใช้ cursor ของ Alice กับบัญชี Bob | ไม่ได้ข้อมูลของ Alice (ได้ของ Bob หรือ 400) | - | - | - | BE-007 contract |
| QA2-I-10-deleted-404 | สนทนาที่ลบแล้ว | เจ้าของเปิดได้ 404 CONVERSATION_NOT_FOUND | 404 | CONVERSATION_NOT_FOUND | - | BE-007 contract |
| QA2-I-11-no-owner-in-dto | DTO ไม่มีฟิลด์เจ้าของ | response ของ conversation/message ไม่มี userId/ownerId/ลำดับภายใน | - | - | - | BE-007 contract |
| QA2-I-12-patch-mass-assign | PATCH ที่มีฟิลด์อื่น (userId, id, createdAt) | เปลี่ยนเฉพาะ title; id/เจ้าของ/เวลาสร้างไม่เปลี่ยน | 200 | - | - | BE-007 contract |
| QA2-I-13-user-admin-endpoint | User เรียก endpoint ระดับ Admin | 403 AUTH_FORBIDDEN ไม่มีข้อมูล | 403 | AUTH_FORBIDDEN | - | BE-006 matrix |
| QA2-I-14-forged-role-cookie | ยกระดับสิทธิ์ด้วย cookie ที่แก้ role | 401 (ลายเซ็น/การเข้ารหัสของ cookie กันไว้) ไม่ใช่ 200/403 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-I-15-login-role-from-account | role มาจากบัญชีฝั่ง server ไม่ใช่จากผู้เรียก | ผู้ใช้ role User ได้ roles=[User] เสมอ | 200 | - | - | BE-006 matrix |

## C. 3.5 CSRF and cross-site requests (46 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-C-chat-sfs-cross-site | chat: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-chat-sfs-same-site | chat: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-chat-origin-evil | chat: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-chat-origin-null | chat: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-chat-origin-http-same-host | chat: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-chat-origin-double | chat: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-sfs-cross-site | stream: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-sfs-same-site | stream: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-origin-evil | stream: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-origin-null | stream: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-origin-http-same-host | stream: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-stream-origin-double | stream: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-sfs-cross-site | conv-create: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-sfs-same-site | conv-create: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-origin-evil | conv-create: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-origin-null | conv-create: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-origin-http-same-host | conv-create: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-create-origin-double | conv-create: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-sfs-cross-site | conv-patch: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-sfs-same-site | conv-patch: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-origin-evil | conv-patch: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-origin-null | conv-patch: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-origin-http-same-host | conv-patch: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-patch-origin-double | conv-patch: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-sfs-cross-site | conv-delete: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-sfs-same-site | conv-delete: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-origin-evil | conv-delete: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-origin-null | conv-delete: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-origin-http-same-host | conv-delete: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-conv-delete-origin-double | conv-delete: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-sfs-cross-site | logout: Sec-Fetch-Site: cross-site | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-sfs-same-site | logout: Sec-Fetch-Site: same-site (เว็บพี่น้อง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-origin-evil | logout: Origin: https://evil.example | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-origin-null | logout: Origin: null (sandbox/ฟอร์มข้ามเว็บ) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-origin-http-same-host | logout: Origin: http://localhost (scheme ต่าง) | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-logout-origin-double | logout: Origin ซ้ำสองค่า | 403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่) | 403 | AUTH_CSRF_INVALID | 0 | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-allow-same-origin | Origin เดียวกัน + Sec-Fetch-Site: same-origin (เหมือน browser) | chat/stream/สร้าง/แก้/ลบ สำเร็จ | 200 | - | - | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-allow-sfs-none | Sec-Fetch-Site: none (ผู้ใช้พิมพ์ URL) | ไม่ถูกบล็อกโดย guard | 200 | - | - | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-allow-no-browser-headers | ไม่มี Origin/Sec-Fetch (สคริปต์/เครื่องมือ) | ไม่ถูกบล็อก 200 (CSRF ต้องมีเบราว์เซอร์; ออกแบบไว้) | 200 | - | - | BE-006 (SameSite guard) + BE-005 (antiforgery) |
| QA2-C-simple-text-plain | ฟอร์มข้ามเว็บแบบ text/plain ที่ไม่มี header เบราว์เซอร์ | 415 และ Ollama 0 (ไม่รับ body ที่ไม่ใช่ JSON) | 415 | - | 0 | ชั้นป้องกันที่สอง (JSON-only) |
| QA2-C-simple-form-urlencoded | ฟอร์ม application/x-www-form-urlencoded | 415 และ Ollama 0 | 415 | - | 0 | ชั้นป้องกันที่สอง (JSON-only) |
| QA2-C-cors-none | CORS: preflight และ response ข้ามเว็บ | ไม่มี Access-Control-Allow-Origin/Credentials ในทุก response | - | - | - | QA baseline |
| QA2-C-stream-fetch-no-token | fetch POST ของหน้าเว็บ (same-origin) ไปที่ /api/chat/stream แล้วอ่านสตรีม | 200 text/event-stream; started, delta, completed ตามลำดับ | 200 | - | 1 | Issue QA-002 3.7 |
| QA2-C-stream-fetch-with-token | เหมือนกัน แต่แนบ X-CSRF-TOKEN จาก /api/auth/csrf | 200 สตรีมสำเร็จเหมือนกัน (token ที่แนบเกินจำเป็นไม่ทำให้พัง) | 200 | - | 1 | Issue QA-002 3.7 |
| QA2-C-method-override | X-HTTP-Method-Override: DELETE บน GET | ไม่ลบอะไร (header ถูกเมิน) | 200 | - | - | QA baseline |
| QA2-C-get-safe | GET ไม่เปลี่ยนสถานะ | เรียก GET ทุก endpoint ที่มี cookie แล้วข้อมูลไม่เปลี่ยน | 200 | - | - | QA baseline |

## H. 3.5 Security headers (12 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-H-01-home | GET / (หน้าแชต) | CSP มี default-src 'self', object-src 'none', frame-ancestors 'none'; X-Content-Type-Options nosniff; Referrer-Policy no-referrer | 200 | - | - | FE-001 |
| QA2-H-02-auth-api | /api/auth/* ทุก endpoint | Cache-Control no-store, Pragma no-cache, nosniff | - | - | - | BE-005 |
| QA2-H-03-401-no-store | 401 AUTH_REQUIRED จาก /api/chat | Cache-Control no-store | 401 | AUTH_REQUIRED | - | BE-006 |
| QA2-H-04-403-no-store | 403 AUTH_FORBIDDEN และ AUTH_CSRF_INVALID | Cache-Control no-store | 403 | - | - | BE-006 |
| QA2-H-05-conversations-no-store | response ของ /api/conversations | Cache-Control no-store | 200 | - | - | BE-007 |
| QA2-H-06-chat-no-store | 200 ของ /api/chat (คำตอบส่วนตัวของผู้ใช้) | Cache-Control no-store (กัน proxy/เบราว์เซอร์เก็บคำตอบ) | 200 | - | - | QA baseline |
| QA2-H-07-stream-headers | /api/chat/stream | text/event-stream; Cache-Control no-cache หรือ no-store; X-Accel-Buffering no | 200 | - | - | BE-004 |
| QA2-H-08-hsts | HSTS ใน Production ผ่าน HTTPS (ชื่อโฮสต์ที่ไม่ใช่ localhost) | มี Strict-Transport-Security | 200 | - | - | QA baseline |
| QA2-H-09-no-powered-by | ไม่เปิดเผยเทคโนโลยี | ไม่มี X-Powered-By / X-AspNet-Version | - | - | - | QA baseline |
| QA2-H-10-html-pages-frame | หน้า HTML อื่นนอกจากหน้าแชต (Privacy) | กัน framing: มี X-Frame-Options หรือ CSP frame-ancestors | 200 | - | - | QA baseline |
| QA2-H-11-api-nosniff | ทุก response ของ /api (chat, stream, conversations, 401/403) | X-Content-Type-Options: nosniff | - | - | - | QA baseline |
| QA2-H-12-error-json | 404/405/415 ใต้ /api | เป็น JSON หรือว่าง ไม่มีหน้า error HTML ของ framework | - | - | - | QA baseline |

## L. Error bodies and logs do not leak (12 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-L-01-ollama-500 | Ollama ตอบ 500 พร้อมข้อความภายใน (พอร์ต เส้นทางเครื่อง) ผ่าน /api/chat | 502 AI_PROVIDER_ERROR; body และ log ไม่มี canary/พอร์ต/host/เส้นทาง | 502 | AI_PROVIDER_ERROR | 1 | AI-005 |
| QA2-L-02-ollama-500-stream | เหมือนกันผ่าน /api/chat/stream | event: error AI_PROVIDER_ERROR; ไม่มีรายละเอียดภายใน | 200 | AI_PROVIDER_ERROR | 1 | AI-005 |
| QA2-L-03-ollama-down | Ollama ไม่ทำงาน (connection refused) ทั้งสอง endpoint | 503 AI_UNAVAILABLE / event error AI_UNAVAILABLE; ไม่มี host:port | 503 | AI_UNAVAILABLE | - | AI-005 |
| QA2-L-04-ollama-garbage | Ollama ตอบ body เสีย | AI_INVALID_RESPONSE; ไม่มีเนื้อหา body เดิม | 502 | AI_INVALID_RESPONSE | - | AI-005 |
| QA2-L-05-userstore-exception | ฐานข้อมูลผู้ใช้โยน exception ที่มี connection string | 401 AUTH_REQUIRED ทั่วไป; body และ log ไม่มี canary | 401 | AUTH_REQUIRED | 0 | BE-006 (SecurityLog ไม่ใส่ข้อความ exception) |
| QA2-L-06-history-exception | ที่เก็บประวัติโยน exception ที่มี connection string (เป็น ChatHistoryUnavailable) | 503 HISTORY_UNAVAILABLE; body และ log ไม่มี canary | 503 | HISTORY_UNAVAILABLE | - | BE-007 |
| QA2-L-07-unhandled-exception | exception ที่ไม่คาดคิดจาก dependency (มีความลับในข้อความ) | 500 INTERNAL_ERROR ทั่วไป; log ไม่มีความลับ | 500 | INTERNAL_ERROR | - | AGENTS.md ข้อ 12 (ไม่ log ความลับ) |
| QA2-L-08-login-failure-logs | log หลัง login ล้มเหลวหลายแบบ | log ไม่มีรหัสผ่านที่ส่งมา hash หรือ stamp | - | - | - | BE-005 |
| QA2-L-09-sweep | กวาด log ทั้งหมดหลัง login/chat/stream/logout/ปฏิเสธต่าง ๆ | log ไม่มีรหัสผ่าน hash stamp session id ค่า cookie หรือ CSRF token | - | - | - | BE-006 SecurityLog |
| QA2-L-10-audit-events | เหตุการณ์ตรวจสอบ | มี log 5001 (สร้าง session), 5002 (logout), 5003 (ปฏิเสธพร้อมเหตุผล), 5005 (ข้ามเว็บ) และไม่มีค่าลับ | - | - | - | BE-006 SecurityLog |
| QA2-L-11-framework-errors | อินพุตผิด (JSON เสีย/ชนิดผิด/ใหญ่เกิน) ที่ /api | 400/413/415 ไม่มี stack trace, ชื่อชนิด .NET หรือเส้นทางไฟล์ | 400 | INVALID_MESSAGE | 0 | AGENTS.md ข้อ 12 |
| QA2-L-12-same-reason-hidden | 401 ของ session ที่ logout / หมดอายุ / ปิดบัญชี / เปลี่ยน role | body เหมือนกันทุกไบต์ (ไม่บอกเหตุผล) | 401 | AUTH_REQUIRED | - | BE-006 matrix |

## U. 3.6 Unauthenticated calls never reach Ollama (9 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-U-01-anon-chat-x10 | ไม่ล็อกอิน เรียก /api/chat 10 ครั้ง | 401 ทุกครั้ง; Ollama 0 | 401 | AUTH_REQUIRED | 0 | Issue QA-002 3.6 |
| QA2-U-02-anon-stream-x10 | ไม่ล็อกอิน เรียก /api/chat/stream 10 ครั้ง | 401 JSON ทุกครั้ง; Ollama 0 | 401 | AUTH_REQUIRED | 0 | Issue QA-002 3.6 |
| QA2-U-03-anon-invalid-body | ไม่ล็อกอิน ส่ง body ว่าง/เสีย | 401 (ตรวจสิทธิ์ก่อนตรวจ body) Ollama 0 | 401 | AUTH_REQUIRED | 0 | Issue QA-002 3.6 |
| QA2-U-04-anon-oversized | ไม่ล็อกอิน ส่ง body 2 MB | ไม่ใช่ 200; Ollama 0 (401 หรือ 413) | - | - | 0 | Issue QA-002 3.6 |
| QA2-U-05-anon-conversation-id | ไม่ล็อกอิน ระบุ conversationId | 401; Ollama 0 | 401 | AUTH_REQUIRED | 0 | Issue QA-002 3.6 |
| QA2-U-06-rejected-never-reach | คำขอที่ถูกปฏิเสธทุกแบบ (CSRF, session สิ้นสุด, ปิดบัญชี, 415) | Ollama 0 หลังชุดคำขอทั้งหมด | - | - | 0 | Issue QA-002 3.6 |
| QA2-U-07-invalid-message | ผู้ใช้ล็อกอิน ส่งข้อความว่าง | 400 INVALID_MESSAGE; Ollama 0 | 400 | INVALID_MESSAGE | 0 | BE-001 |
| QA2-U-08-counter-sanity | ตรวจตัวนับของ fake Ollama (คำขอถูกต้องของผู้ใช้) | chat 1 ครั้ง stream 1 ครั้ง นับได้ 1 ต่อคำขอ | 200 | - | 1 | ตรวจเครื่องมือวัด |
| QA2-U-09-http-scheme | เรียก /api/chat ผ่าน HTTP | ไม่ใช่ 200; Ollama 0 (redirect หรือ 401) | - | - | 0 | BE-005 |

## MS. 3.7 Session ended during a stream (14 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-MS-logout | logout จากอีกแท็บระหว่างสตรีม | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-revoke-all | เพิกถอนทุก session ระหว่างสตรีม | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-disabled | ปิดบัญชีระหว่างสตรีม (ตรวจบัญชีทุก 5 วินาที) | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-role | เปลี่ยน role ระหว่างสตรีม | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-stamp | เปลี่ยน security stamp ระหว่างสตรีม | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-absolute | ครบ 8 ชั่วโมงระหว่างสตรีม | event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-reuse | ใช้ cookie เดิมหลังสตรีมถูกตัด | 401 และ Ollama ไม่ถูกเรียกเพิ่ม | 401 | AUTH_REQUIRED | 1 | Issue QA-002 3.7 |
| QA2-MS-store-down | ฐานข้อมูลผู้ใช้ล่มระหว่างสตรีม | สตรีมจบด้วย error AUTH_SESSION_ENDED (fail closed); session ไม่ถูกเพิกถอน ฐานกลับมาแล้วใช้ต่อได้ | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-long-active | สตรีมยาว 40 นาทีที่ยังส่งข้อความอยู่ | จบ completed (ไม่ถูกตัดด้วย idle 30 นาที) | 200 | - | 1 | Issue QA-002 3.7 |
| QA2-MS-stalled-model | เพิกถอน session ขณะโมเดลเงียบ (กำลังคิด ไม่ส่งข้อความใหม่) | สตรีมจบด้วย error AUTH_SESSION_ENDED ภายใน 10 วินาทีหลังเพิกถอน แม้โมเดลไม่ส่งข้อความเพิ่ม | 200 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-history | สตรีมในสนทนา แล้ว session สิ้นสุด | ข้อความตอบถูกบันทึกเป็นสถานะ failed รหัส AUTH_SESSION_ENDED พร้อมข้อความที่ส่งไปแล้ว ไม่ใช่ completed | 200 | AUTH_SESSION_ENDED | 1 | BE-007 + Issue QA-002 3.7 |
| QA2-MS-chat | session สิ้นสุดระหว่างรอ /api/chat | 401 AUTH_SESSION_ENDED ไม่ส่งคำตอบ | 401 | AUTH_SESSION_ENDED | 1 | Issue QA-002 3.7 |
| QA2-MS-other-user | เพิกถอน session ของคนอื่นระหว่างสตรีมของ Alice | สตรีมของ Alice จบ completed | 200 | - | 1 | Issue QA-002 3.7 |
| QA2-MS-wire-format | รูป event ปิดท้ายหลังเพิกถอน | เป็น SSE ถูกต้อง: event: error, data เป็น JSON มี code/message/partial | 200 | AUTH_SESSION_ENDED | 1 | BE-004 SSE contract |

## Q. 3.6 One user flooding / queue messages (3 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-Q-01-one-user-many | ผู้ใช้หนึ่งคนยิงสตรีม 6 คำขอพร้อมกัน ขณะผู้ใช้อื่น 1 คำขอ (Ollama จำลองทำทีละงาน) | ทุกคำขอจบ completed หรือ error ที่มี code ตามสัญญา; ไม่มี 500/INTERNAL_ERROR; ผู้ใช้อื่นไม่ค้างเกิน 30 วินาที | 200 | - | 7 | Issue QA-002 3.6 |
| QA2-Q-02-timeout-message | คิวยาวจนเลย timeout ของ Ollama | ผู้ใช้ที่ช้าได้ event error code AI_TIMEOUT พร้อมข้อความที่อ่านรู้เรื่อง ไม่ใช่การตัดการเชื่อมต่อเงียบ ๆ | 200 | AI_TIMEOUT | - | Issue QA-002 3.6 / BE-003 |
| QA2-Q-03-per-user-cap | มีเพดานคำขอพร้อมกันต่อผู้ใช้ | ผู้ใช้เดียวไม่สามารถยึดคิวทั้งหมด: อย่างน้อยบางคำขอส่วนเกินถูกปฏิเสธด้วย code ที่เข้าใจได้ (เช่น 429) (QA baseline; ถ้าไม่มีให้บันทึกเป็นข้อสังเกต) | 429 | - | - | QA baseline |

## O. Authentication switched off / configuration (7 กรณี)

| ID | กรณี | Expected | HTTP | Code | Ollama | ที่มา |
| --- | --- | --- | ---: | --- | ---: | --- |
| QA2-O-01-auth-off-routes | Authentication:Enabled=false: /api/auth/* | 404 ทั้ง csrf และ login (ไม่เปิดเผยว่ามี) | 404 | - | 0 | BE-005 |
| QA2-O-02-auth-off-chat-open | Authentication:Enabled=false: /api/chat และ /stream โดยไม่ล็อกอิน | 200 (ค่าเริ่มต้นเปิดโล่งตามที่ระบุไว้ใน BE-006) | 200 | - | 2 | BE-006 matrix (ค่าเริ่มต้น) |
| QA2-O-03-auth-off-admin | Authentication:Enabled=false: endpoint Admin โดยไม่ล็อกอิน | ยังต้องล็อกอิน: 401 | 401 | AUTH_REQUIRED | - | BE-006 matrix |
| QA2-O-04-history-needs-auth | ChatHistory:Enabled=true แต่ Authentication:Enabled=false | แอปไม่เริ่ม (ตรวจ options ตอนเริ่ม ล้มอย่างปิด) | - | - | - | BE-007 |
| QA2-O-05-history-off-404 | ChatHistory:Enabled=false (ค่าเริ่มต้น): /api/conversations | 404 ก่อนตรวจสิทธิ์ ทั้งผู้ไม่ล็อกอินและผู้ล็อกอิน | 404 | - | 0 | BE-007 |
| QA2-O-06-history-off-chat-conv | ChatHistory ปิด แต่ส่ง conversationId | 404 CONVERSATION_NOT_FOUND; Ollama 0 | 404 | CONVERSATION_NOT_FOUND | 0 | BE-007 |
| QA2-O-07-production-default-open | ค่าเริ่มต้นใน appsettings.json | บันทึกเป็นข้อสังเกต: Authentication.Enabled=false เป็นค่าเริ่มต้น จึงต้องเปิดก่อนใช้กับผู้ใช้จริง | - | - | - | BE-006 matrix |

คอลัมน์ HTTP/Code/Ollama: `-` คือไม่ได้กำหนดค่านั้นเป็นเกณฑ์ (ตัดสินด้วยข้อความ Expected) · Ollama คือจำนวนคำขอที่ fake Ollama ต้องได้รับ

รวม 256 กรณี
