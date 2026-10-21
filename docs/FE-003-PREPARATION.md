# FE-003 — Login UI groundwork (Mock only)

วันที่2026-10-21 · [Issue24](https://github.com/b3amkawi/BC-LocalAI/issues/24)
· branch `watzapon_FE-003` · base `origin/main` = `0cb07ff20308feb0c3ac50686cbf8c96d85a322e`

## ขอบเขตที่ทำล่วงหน้า

**ยังไม่ใช่ FE-003 ที่เสร็จครบ และไม่ใช่ Login สำหรับใช้งานจริง**
Issue อนุญาตให้เริ่มด้วย Mock เมื่อมี contract ที่ตกลงแล้ว จึงเตรียมเฉพาะ Login UI,
auth state, profile, Logout และ error handling ตาม [BE-005 contract](authentication/BE-005-INTEGRATION.md)
โดยไม่แก้ Session/Auth backend, Chat เดิม หรือไฟล์ของทีม AI

ใช้ placeholder branch เดิมที่ยังไม่มีงานเฉพาะ แล้วแยก worktreeและรัน `git pull --ff-only origin main` สำเร็จก่อนเริ่มงาน
ไม่สลับ branch ในโฟลเดอร์หลักที่มีงานอื่น และไม่แตะ worktree BE-006

| Deliverable | สิ่งที่มีใน branch นี้ | สิ่งที่ยังรอ |
|---|---|---|
| D1 Login UI / Authentication State | ฟอร์ม, validation, loading, กัน submit ซ้ำ, five-state controller แบบ Mock | ต่อ API จริงและทดสอบ cookie/session |
| D2 Authenticated Layout | ตัวอย่าง profile/Logout ที่แยกจาก Chat | Protected Views, authenticated Chat และ session จริง BE-006 |
| D3 Conversation Sidebar | ยังไม่ทำ | contract ที่ตกลงและ BE-007 |
| D4 Message History | ยังไม่ทำ | BE-007 persistence/API และ resource authorization |
| D5 Streaming Integration | ไม่เปลี่ยน FE-002 เดิม | Conversation ID, Resume Chat และ BE-007 |
| D6 Tests | Unit, DOM-shell และ preview route integration สำหรับขอบเขตนี้ | E2E ที่ใช้ backend จริงและ history |
| D7 Verification | [รายงาน](FE-003-VERIFICATION.md) และภาพจาก browser | Authentication/History จริงและ UAT |

ไม่ติ๊กปิด issue/Acceptance Criteria ที่ต้องใช้ระบบจริง และไม่อ้างว่า Mock พิสูจน์เรื่อง cross-user isolation ของ server แล้ว

## วิธีเปิด preview ในเครื่อง

เปิด PowerShell ใหม่ใน root ของ worktree นี้ (ไม่ใช้บัญชีหรือรหัสผ่านจริง):

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5936'
$env:FrontendPreview__Enabled = 'true'
$env:Authentication__Enabled = 'false'
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --no-launch-profile
```

เปิด `http://127.0.0.1:5936/preview/login` ใช้ชื่อสมมติ `demo.user` และข้อความสมมติใดก็ได้เป็นรหัสผ่าน
Mock ไม่ตรวจรหัสจริง จึงไม่ต้องติดตั้ง MySQL หรือเรียก Ollama สำหรับหน้านี้
เลือกสถานการณ์ success,401,403,CSRF,429,503,network หรือ expired ได้
กรณี expired ให้ Login แล้วกดตรวจสอบเซสชัน

หยุดด้วย Ctrl+C และปิด PowerShell นี้เพื่อทิ้ง environment flags
ไม่มีการบันทึก flag ลง appsettings หรือเปิด preview แบบถาวร
ห้าม bind ไป `0.0.0.0`, เปิด port ให้ LAN หรือวาง preview หลัง reverse proxy

## โครงสร้างและข้อควรระวัง

- `LoginPreviewController`: GET `/preview/login` ต้องเป็น Development + explicit flag + remote loopback จึงเปิดได้
  (IPv4,IPv6,mapped IPv4); กรณีอื่น404 รวมถึง Production แม้เปิด flag
- `Views/LoginPreview/Index.cshtml`, `auth-preview.css`: UI ภาษาไทยแยก layout เดิม ชัดเจนว่าเป็น Mock
- `auth-core.js`: validation/DTO whitelist, API client ที่ต้อง inject transport และ auth state controller
- `auth-mock.js`: transport ใน memory ไม่มี fetch, cookie, storage หรือสิทธิ์ใช้งานจริง
- `auth-preview.js`: DOM shell ใช้ `textContent`, ล้าง profile เก่าทันทีเมื่อ recheck/fail/logout/navigation
- ฟอร์มใช้ labels, error alert, status live region, focus และ responsive layout ตาม framework เดิม
- state มี checking/authenticated/unauthenticated/expired/error; operation เดียวในขณะหนึ่ง,
  abort และ generation guard ป้องกันผลเก่ากลับมาแสดงเป็นผู้ใช้อีกคน
- password ไม่ trim/normalize; ไม่อยู่ใน state/storage/log และล้างช่องทันทีหลัง submit
- มี CSRF GET ก่อน POST ทุกครั้ง, fresh token หลังเปลี่ยน principal, ไม่ retry password อัตโนมัติ
- request shape เตรียม same-origin cookies/no-store/redirect-error แต่ **ไม่มี real transport หรือ real cookie ใน preview**
- profile เก็บเฉพาะ public DTO; roles เป็น presentation เท่านั้น ไม่ใช่ client-side authorization
- CSP `connect-src 'none'` และ `form-action 'none'` กันทั้ง network และ native form submission จากหน้า preview
- ไม่มี localStorage/sessionStorage/CDN/dependency ใหม่ และไม่เปลี่ยน configuration ของ Login จริง
- refresh/new page ทำให้ Mock กลับ anonymous โดยตั้งใจ ไม่ใช่หลักฐาน session persistence

## ขั้นถัดไปเมื่อ dependency พร้อม

1. ทบทวน BE-006 contract ที่ merge จริงก่อนต่อ HTTPS transport, antiforgery และ cookie/session
2. เพิ่ม timeout/cancellation และ real integration tests รวม refresh, Logout, expiry,401/403,CSRF และหลาย tab
3. ทำ protected layout/Chat โดยยังบังคับสิทธิ์บน server และทดสอบการสลับบัญชี/ยกเลิก stream
4. เมื่อตกลง BE-007 API แล้วจึงทำ Sidebar/History/Resume/Conversation ID และ cross-user resource tests
5. รัน browser/E2E/UAT ระบบจริงก่อนสรุป FE-003 ครบ; ห้ามเพียงเปลี่ยน Mock เป็น fetch ใน preview นี้

ไม่ push,เปิด PR,merge หรือ deploy ในงานรอบนี้โดยไม่มีคำสั่งเพิ่มเติม
