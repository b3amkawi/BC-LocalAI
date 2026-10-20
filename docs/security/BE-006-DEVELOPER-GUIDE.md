# BE-006 — Developer Guide: sessions and authorization (D7)

Issue: #22 · Code: `BC-LocalAI/Security/` · Matrix: [BE-006-AUTHORIZATION-MATRIX.md](BE-006-AUTHORIZATION-MATRIX.md)

## 1. ส่วนประกอบ

| ไฟล์ | หน้าที่ |
|---|---|
| `SecurityModels.cs` | `BcRoles` (User/Admin), `BcPolicies` (ChatUser/Admin), `SessionRecord`, `UserSecurityState`, เหตุผลการปฏิเสธ |
| `BcAuthenticationOptions.cs` | ส่วน `Authentication` ใน appsettings: `Enabled` (false), `IdleTimeoutMinutes` (30), `AbsoluteLifetimeMinutes` (480) + validator ตอนเริ่มระบบ |
| `SessionValidator.cs` | ตัดสินว่า session ใช้ได้ไหม (pure function, ทดสอบด้วยเวลาคงที่) |
| `SessionStore.cs` | `ISessionStore` + `InMemorySessionStore` (session id สุ่ม 256 bit), `IUserSecurityStateSource` |
| `SessionService.cs` | `SignInAsync` / `SignOutAsync` / `RevokeAllForUserAsync` |
| `BcCookieEvents.cs` | ตรวจ session + บัญชีทุก request ที่มี cookie; เปลี่ยน redirect เป็น JSON 401/403 |
| `SameSiteRequestGuard.cs` | ปฏิเสธ POST/PUT/PATCH/DELETE ไป `/api` ที่มาจากเว็บอื่น (เมื่อเปิด auth) |
| `BcAuthentication.cs` | `AddBcLocalAiSecurity()` / `UseBcLocalAiSecurity()`, cookie options, policy handlers |
| `SecurityLog.cs` | audit log event 5001–5007 |

Cookie: `__Host-BC.LocalAI`, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, ไม่มี `Domain`, ไม่มี `Expires` (หมดเมื่อปิด browser),
เนื้อใน cookie เข้ารหัสด้วย ASP.NET Core Data Protection และมีแค่ user id, role, session id (ไม่มีรหัสผ่าน/hash/stamp)

## 2. สิ่งที่ BE-005 ต้องทำเพื่อต่อเข้ากัน

1. **ใช้งาน `IUserSecurityStateSource`** อ่าน `id, is_active, lockout_end_utc, role, security_stamp` จาก `users` ด้วยบัญชี `bc_auth_reader`
   (parameterized query, รองรับ cancellation) แล้ว register ใน `Program.cs`; คืน `null` ถ้าไม่พบผู้ใช้ และ **throw** ถ้าฐานติดต่อไม่ได้ (ระบบจะปฏิเสธเอง)
2. **Login:** ตรวจรหัสผ่าน + สถานะบัญชีเองก่อน แล้วเรียก `SessionService.SignInAsync(HttpContext, user)` (สร้าง session ใหม่ทุกครั้ง กัน session fixation)
3. **Logout:** เรียก `SessionService.SignOutAsync(HttpContext)` (เพิกถอนฝั่ง server + ลบ cookie)
4. **`/api/auth/me`:** อ่าน `ClaimTypes.NameIdentifier` และ `ClaimTypes.Role` จาก `HttpContext.User` (ผ่านการตรวจ session แล้ว)
5. endpoint ที่ไม่ต้อง login ใส่ `[AllowAnonymous]`; ที่ต้อง login ใส่ `[Authorize(Policy = BcPolicies.ChatUser)]` หรือ `BcPolicies.Admin`
6. Antiforgery token ของ `/api/auth/*` เป็นของ BE-005 ตาม contract SEC-001 (guard ของ BE-006 เป็นอีกชั้นหนึ่ง ไม่ใช่ตัวแทน)
7. เมื่อปิดบัญชี / reset รหัส / เปลี่ยน role: เปลี่ยน `security_stamp` และเรียก `RevokeAllForUserAsync(userId)`

ห้ามใช้ role หรือ user id จาก request body เพื่อตัดสินสิทธิ์ ใช้จาก `HttpContext.User` เท่านั้น

## 3. เปิดใช้งาน (หลัง BE-005 + FE-003 พร้อม)

1. BE-005 merge แล้วและ register `IUserSecurityStateSource` (ถ้าไม่มี แอปจะ**ไม่ยอมเริ่ม**เมื่อเปิด auth — ตั้งใจ)
2. ใช้ HTTPS (cookie เป็น `Secure`; บน http จะไม่ถูกส่ง)
3. ตั้ง `Authentication__Enabled=true` (หรือใน appsettings ของเครื่องนั้น)
4. ทดสอบ: ไม่ login → `/api/chat` ได้ 401, login แล้วได้ 200, logout แล้ว cookie เดิมใช้ไม่ได้

## 4. ข้อจำกัดที่ตั้งใจ (ตัดสินแล้ว 2026-10-20)

- **Session อยู่ในหน่วยความจำ:** รีสตาร์ทแอป = ทุกคนต้อง login ใหม่; รองรับแอป instance เดียว
  ถ้าต้องการหลาย instance ให้ทำ `ISessionStore` บน MySQL (ตาราง sessions + บัญชี writer แยก) — interface เตรียมไว้แล้ว
- **Data Protection keys** ใช้ค่าเริ่มต้นของ ASP.NET Core (ผูกกับเครื่อง/ผู้ใช้ที่รันแอป) ซึ่งสอดคล้องกับ session ในหน่วยความจำ
  ถ้าย้ายเครื่องหรือหลาย instance ต้องกำหนดที่เก็บ key ที่จำกัดสิทธิ์
- **Stream ที่เปิดอยู่:** สิทธิ์ตรวจตอนเริ่ม request; ถ้าบัญชีถูกปิดระหว่างที่คำตอบกำลังสตรีม คำตอบนั้นจะจบตามปกติ
  (ไม่เกิน deadline ของ Ollama 180 วินาที) และ request ถัดไปจะถูกปฏิเสธทันที
- ตรวจบัญชี **ทุก request** (ไม่ cache) ตามการตัดสินใจ: 1 query เล็กต่อ request
- ตัวเลข lockout/rate limit ของ login เป็นของ BE-005
