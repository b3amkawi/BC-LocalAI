# SEC-001 — Authentication API Contract (Proposed, not implemented)

วันที่: 2026-10-20 · ส่งต่อ BE-005/006 และ FE-003 · [Scope](legacy-auth-review.md)

ใช้ same-origin HTTPS + Cookie Authentication ไม่ใช้ JWT/localStorage
คำตอบทุก auth endpoint มี `Cache-Control: no-store`; error ใช้รูปแบบ `ApiErrorResponse` เดิม:

```json
{"error":{"code":"AUTH_INVALID_CREDENTIALS","message":"ชื่อบัญชีหรือรหัสผ่านไม่ถูกต้อง"}}
```

## Endpoints

| Method / Route | Auth / CSRF | Request / Response |
|---|---|---|
| GET `/api/auth/csrf` | anonymous ได้; same-origin เท่านั้น | 200 `{"token":"<request-token>"}` + antiforgery cookie; ไม่ใช่ auth session |
| POST `/api/auth/login` | anonymous ได้; ต้อง `X-CSRF-TOKEN` | JSON `username`, `password`; 200 user DTO และ Set-Cookie ใหม่ |
| GET `/api/auth/me` | authenticated | 200 safe user DTO; 401 เมื่อไม่มี/หมดอายุ/revoked |
| POST `/api/auth/logout` | valid CSRF; ไม่ต้องมี active session เพื่อให้ idempotent | 204 ไม่มี body; revoke ถ้ามีและ clear cookie |

ตัวอย่าง user DTO (ข้อมูลสมมติ):

```json
{"user":{"id":"<stable-user-id>","username":"sec001.active","displayName":"Synthetic active","roles":["User"]}}
```

ไม่ส่ง password, hash, salt, stamp, raw session ID, DB schema/connection information
GET ห้ามเปลี่ยนสถานะ Login/Logout; ส่ง token ใน header ไม่ใช่ URL
หลัง Login/Logout ให้ดึง CSRF token ใหม่ก่อน mutation ถัดไป; ไม่ reuse token ของ principal เดิม

## Validation และ status codes

- `Content-Type: application/json`; reject malformed/null body, fields ผิดชนิด/ซ้ำ และ body ใหญ่เกินขอบเขต
- เสนอ request body ≤16 KiB; username 1–128 ASCII `[A-Za-z0-9._-]`; password 1–1024 ตัวอักษรและ byte limit ใน service
- ห้าม trim/normalize/case-fold password; username normalize ตาม Architecture; ไม่ใช้ข้อความที่ผู้ใช้ส่งมาเป็น SQL identifier
- จำกัด concurrency/attempt ก่อน expensive hashing; ตัวเลข rate/lockout ต้อง review ตาม requirements ไม่ฝังค่าลับ

| Status | Code | กรณี |
|---|---|---|
| 400 | AUTH_INVALID_REQUEST | validation, malformed JSON; ไม่ echo input |
| 401 | AUTH_INVALID_CREDENTIALS | unknown/wrong/disabled/locked ใช้ข้อความเดียวกัน |
| 401 | AUTH_REQUIRED | session ไม่มี/หมดอายุ/revoked |
| 403 | AUTH_FORBIDDEN | authenticated แต่ไม่มี permission |
| 403 | AUTH_CSRF_INVALID | mutation ที่ CSRF ไม่ผ่าน รวม Login/Logout |
| 413 | REQUEST_TOO_LARGE | body เกิน limit ก่อน deserialize |
| 429 | AUTH_RATE_LIMITED | rate limit พร้อม Retry-After ที่มีขอบเขต |
| 503 | AUTH_UNAVAILABLE | identity/session dependency unavailable; fail closed |
| 500 | INTERNAL_ERROR | unexpected safe error + correlation metadata ไม่ส่ง stack trace |

หมายเหตุสำหรับ implementation: `Program.cs` ปัจจุบัน map invalid ModelState ทุก API เป็น `INVALID_MESSAGE`
BE-005 ต้องทำ auth-specific validation/error mapping โดยไม่เปลี่ยน Chat API contract
รวมถึง override API Cookie challenge/forbid ให้เป็น 401/403 JSON ไม่ redirect ไปหน้า HTML

## Boundary กับ Streaming

CSRF และ Authentication/Authorization ต้องสำเร็จก่อน `/api/chat/stream` เริ่มส่ง event
เมื่อยังไม่เปิด stream ใช้ HTTP JSON 401/403 ตามตาราง; ถ้าเริ่มไปแล้วห้ามเปลี่ยน HTTP status ย้อนหลัง
BE-006 ต้องกำหนด terminal auth-expiry event/connection close และ cancellation ตาม contract ที่ FE-002/FE-003 ตกลง
ยังไม่เพิ่ม event ใหม่เองใน SSE contract ของ BE-004

## Security requirements

ไม่เปิด credentialed CORS กว้าง ๆ, ตรวจ Origin/Fetch Metadata เป็น defense-in-depth แต่ไม่แทน antiforgery
cookie Secure/HttpOnly/SameSite ตาม Architecture, ไม่รับ role/user ID จาก body เพื่อกำหนดสิทธิ์
account enumeration ตรวจทั้ง status/body/เวลาประมวลผล ไม่เปิดเผยรายละเอียดผ่าน log
ไม่มี Register/Reset Password/Admin CRUD endpoint ใน Issue นี้
