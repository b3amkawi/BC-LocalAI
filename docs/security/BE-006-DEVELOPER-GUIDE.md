# BE-006 — Developer Guide: sessions and authorization (D7)

Issue: #22 · Code: `BC-LocalAI/Security/` · Matrix: [BE-006-AUTHORIZATION-MATRIX.md](BE-006-AUTHORIZATION-MATRIX.md)

## 1. ส่วนประกอบ

| ไฟล์ | หน้าที่ |
|---|---|
| `SecurityModels.cs` | `BcRoles` (User/Admin), `BcPolicies` (ChatUser/Admin), `SessionRecord`, `UserSecurityState`, เหตุผลการปฏิเสธ |
| `BcAuthenticationOptions.cs` | ส่วน `Authentication` ใน appsettings: `Enabled` (false), `IdleTimeoutMinutes` (30), `AbsoluteLifetimeMinutes` (480) + validator ตอนเริ่มระบบ |
| `SessionValidator.cs` | ตัดสินว่า session ใช้ได้ไหม (pure function, ทดสอบด้วยเวลาคงที่) |
| `SessionStore.cs` | `ISessionStore` + `InMemorySessionStore` (session id สุ่ม 256 bit, จำกัดจำนวนตาม `Authentication:MaxSessions` ของ BE-005), `IUserSecurityStateSource` |
| `MySqlUserSecurityStateSource.cs` | อ่านสถานะบัญชีจากตาราง `users` ด้วยบัญชี reader ของ BE-005 (`Authentication:ReaderConnectionString`) |
| `SessionService.cs` | `SignInAsync` / `SignOutAsync` / `RevokeAllForUserAsync` |
| `BcCookieEvents.cs` | ตรวจ session + บัญชีทุก request ที่มี cookie; เปลี่ยน redirect เป็น JSON 401/403 |
| `SameSiteRequestGuard.cs` | ปฏิเสธ POST/PUT/PATCH/DELETE ไป `/api` ที่มาจากเว็บอื่น (เมื่อเปิด auth) |
| `BcAuthentication.cs` | `AddBcLocalAiSecurity()` / `UseBcLocalAiSecurity()`, cookie options, policy handlers |
| `SecurityLog.cs` | audit log event 5001–5007 |

Cookie: `__Host-BC.LocalAI`, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, ไม่มี `Domain`, ไม่มี `Expires` (หมดเมื่อปิด browser),
เนื้อใน cookie เข้ารหัสด้วย ASP.NET Core Data Protection และมีแค่ user id, role, session id (ไม่มีรหัสผ่าน/hash/stamp)
Cookie และ header `Authorization` ของ browser **ไม่ถูกส่งต่อไป Ollama** (`OllamaCookieIsolationTests`); ห้ามเพิ่มการส่งต่อ header ของผู้ใช้ไปยัง Ollama client

## 2. การต่อกับ BE-005 (รวมแล้วใน branch นี้)

BE-005 (dc477f7) ถูก merge เข้า `watzapon_BE-006` แล้ว ทั้งสองส่วนใช้ cookie scheme เดียวกัน (`BC.LocalAI`, cookie `__Host-BC.LocalAI`):

1. **Login** (`AuthenticationController`): BE-005 ตรวจรหัสผ่าน/lockout/rate limit แล้วเรียก `SessionService.SignInAsync(HttpContext, userId, profileClaims)`
   ซึ่ง**อ่านบัญชีจาก MySQL อีกครั้ง**: ไม่พบ / ปิดอยู่ / ล็อก / role ไม่รู้จัก / ไม่มี stamp → คืน false → 401 `AUTH_INVALID_CREDENTIALS` แบบเดียวกับรหัสผิด และไม่ออก cookie
2. **Role มาจากคอลัมน์ `users.role` เท่านั้น** ไม่ใช่จาก backend ของ login; claim ที่ส่งมาเป็น profile ห้ามทับ NameIdentifier / Role / session id (ถูกกรองทิ้ง)
3. **Logout:** `SessionService.SignOutAsync(HttpContext)` (เพิกถอนฝั่ง server + ลบ cookie)
4. **Session เต็ม** (`MaxSessions`): ล้าง session ที่หมดอายุ/ถูกเพิกถอนก่อน ถ้ายังเต็ม → `AuthenticationDependencyException` → login ได้ 503 `AUTH_UNAVAILABLE` (fail closed)
5. ticket store เดิมของ BE-005 (`BasicAuthenticationTicketStore`) และ `OnValidatePrincipal` ของ BE-005 ถูกแทนด้วย session ของ BE-006 (ตรวจบัญชีทุก request)
6. Antiforgery `X-CSRF-TOKEN` + HTTPS ของ `/api/auth/*` ยังเป็นของ BE-005 (`AuthenticationBoundaryMiddleware`); same-site guard ของ BE-006 เป็นอีกชั้นหนึ่ง
7. endpoint ที่ไม่ต้อง login ใส่ `[AllowAnonymous]`; ที่ต้อง login ใส่ `[Authorize(Policy = BcPolicies.ChatUser)]` หรือ `BcPolicies.Admin`
8. เมื่อปิดบัญชี / reset รหัส / เปลี่ยน role ในฐาน: เปลี่ยน `security_stamp` (session เดิมจะถูกปฏิเสธที่ request ถัดไปอยู่แล้ว) และเรียก `RevokeAllForUserAsync(userId)` ถ้าทำผ่านแอป

ห้ามใช้ role หรือ user id จาก request body เพื่อตัดสินสิทธิ์ ใช้จาก `HttpContext.User` เท่านั้น

## 3. เปิดใช้งาน (หลัง FE-003 พร้อม)

1. ตั้งค่า BE-005 ให้ครบ (`ReaderConnectionString`, `KeyDirectory` ฯลฯ ดู `docs/authentication/BE-005-INTEGRATION.md`); ถ้าไม่มี `IUserSecurityStateSource` แอปจะ**ไม่ยอมเริ่ม**เมื่อเปิด auth — ตั้งใจ
2. ใช้ HTTPS (cookie เป็น `Secure`; บน http จะไม่ถูกส่ง)
3. ตั้ง `Authentication__Enabled=true` (หรือใน appsettings ของเครื่องนั้น)
4. ทดสอบ: ไม่ login → `/api/chat` ได้ 401, login แล้วได้ 200, logout แล้ว cookie เดิมใช้ไม่ได้

## 4. ข้อจำกัดที่ตั้งใจ (ตัดสินแล้ว 2026-10-20)

- **Session อยู่ในหน่วยความจำ:** รีสตาร์ทแอป = ทุกคนต้อง login ใหม่; รองรับแอป instance เดียว
  ถ้าต้องการหลาย instance ให้ทำ `ISessionStore` บน MySQL (ตาราง sessions + บัญชี writer แยก) — interface เตรียมไว้แล้ว
- **Data Protection keys** ใช้ที่เก็บของ BE-005 (`Authentication:KeyDirectory`, เข้ารหัส DPAPI)
- **คำตอบที่กำลังสร้าง (stream / chat):** `StreamSessionGuard` ตรวจซ้ำระหว่างคำตอบ — session (ในหน่วยความจำ) ก่อน**ทุกชิ้น**ของคำตอบ,
  บัญชีใน MySQL ทุก **5 วินาที** (`AccountCheckInterval`) นับจากตอนเริ่ม request; ถ้า session จบ stream ส่ง `event: error`
  `AUTH_SESSION_ENDED` แล้วหยุด (ไม่ส่งข้อความต่อ, ปิดการอ่านจาก Ollama) และ `/api/chat` ตอบ 401 `AUTH_SESSION_ENDED` แทนคำตอบ
  — sign-out / revoke-all / ครบ 8 ชม. หยุดที่ชิ้นถัดไป; ปิด/ลบบัญชี/เปลี่ยน stamp หรือ role หยุดภายใน 5 วินาที;
  ฐานข้อมูลล่ม → หยุด (fail closed) แต่ไม่ revoke; idle timeout ไม่ตัดคำตอบที่กำลังสร้าง (ผู้ใช้กำลังใช้งานอยู่)
- ตรวจบัญชี **ทุก request** (ไม่ cache) ตามการตัดสินใจ: 1 query เล็กต่อ request + ระหว่างคำตอบยาว 1 query ทุก 5 วินาที
- ตัวเลข lockout/rate limit ของ login เป็นของ BE-005 (ใส่รหัสผิด 5 ครั้ง ล็อก 15 นาที)
- **Lockout กันแค่การ login ใหม่** ไม่ตัด session ที่ login อยู่แล้ว (ไม่อย่างนั้นใครก็ได้ที่รู้ username พิมพ์รหัสผิด 5 ครั้งก็เตะเจ้าของออกได้);
  ถ้า Admin ต้องการตัด session ให้ปิดบัญชี (`is_active = 0`) หรือหมุน `security_stamp` — ทำผ่านสคริปต์ SEC-003 (`disable` / `revoke-sessions`, ดู [SEC-003-ADMIN-GUIDE.md](SEC-003-ADMIN-GUIDE.md))
