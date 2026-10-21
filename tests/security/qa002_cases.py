#!/usr/bin/env python3
"""QA-002 case catalog: every case and its EXPECTED result, written before any test was run.

Expected comes from the approved design (docs/security/BE-006-AUTHORIZATION-MATRIX.md, BE-005 and BE-007
contracts, Issue QA-002) or, where marked "QA baseline", from common practice for cookie-authenticated APIs.
Actual results are recorded separately by the test run (docs/qa/qa-002-evidence.json). Do not edit an
Expected value after seeing a result: record a defect in docs/qa/qa-002-defects.md instead.

Usage:
    python tests/security/qa002_cases.py          # rewrite tests/security/qa002-cases.json from this file
    python tests/security/qa002_cases.py --check  # exit 1 when the JSON is out of date
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
JSON_PATH = HERE / "qa002-cases.json"

GROUPS = {
    "M": "3.1 Role x endpoint matrix",
    "A": "3.2 Authentication (login, failure messages, rate limit, lockout, logout)",
    "S": "3.3 Session (expiry, revocation, cookie flags, fixation)",
    "I": "3.4 Authorization, IDOR, privilege escalation",
    "C": "3.5 CSRF and cross-site requests",
    "H": "3.5 Security headers",
    "L": "Error bodies and logs do not leak",
    "U": "3.6 Unauthenticated calls never reach Ollama",
    "MS": "3.7 Session ended during a stream",
    "Q": "3.6 One user flooding / queue messages",
    "O": "Authentication switched off / configuration",
}

CASES: list[dict] = []


def add(cid, group, title, expected, status=None, code=None, ollama=None, basis="BE-006 matrix"):
    CASES.append({
        "id": cid, "group": group, "title": title, "expected": expected,
        "status": status, "code": code, "ollama": ollama, "basis": basis,
    })


# --------------------------------------------------------------------------- M: role x endpoint
# (endpoint label, key, {role: (status, code)}). Ollama column: calls expected at the fake Ollama.
ANON_401 = (401, "AUTH_REQUIRED")
MATRIX = [
    ("GET /", "home", {"anonymous": (200, None), "user": (200, None), "admin": (200, None)}, None),
    ("GET /api/auth/csrf", "csrf", {"anonymous": (200, None), "user": (200, None), "admin": (200, None)}, None),
    ("GET /api/auth/me", "me", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None),
                                "logged-out": ANON_401, "disabled": ANON_401, "idle-expired": ANON_401,
                                "absolute-expired": ANON_401, "role-changed": ANON_401}, None),
    ("POST /api/auth/logout", "logout", {"anonymous": (204, None), "user": (204, None), "admin": (204, None)}, None),
    ("POST /api/chat", "chat", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None),
                                "logged-out": ANON_401, "disabled": ANON_401, "idle-expired": ANON_401,
                                "absolute-expired": ANON_401, "role-changed": ANON_401}, "chat"),
    ("POST /api/chat/stream", "stream", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None),
                                         "logged-out": ANON_401, "disabled": ANON_401, "idle-expired": ANON_401,
                                         "absolute-expired": ANON_401, "role-changed": ANON_401}, "chat"),
    ("GET /api/conversations", "conv-list", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None),
                                             "logged-out": ANON_401, "disabled": ANON_401}, None),
    ("POST /api/conversations", "conv-create", {"anonymous": ANON_401, "user": (201, None), "admin": (201, None)}, None),
    ("GET /api/conversations/{own}", "conv-get-own", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None)}, None),
    ("GET /api/conversations/{other user's}", "conv-get-other", {"anonymous": ANON_401, "user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, None),
    ("PATCH /api/conversations/{own}", "conv-patch-own", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None)}, None),
    ("PATCH /api/conversations/{other user's}", "conv-patch-other", {"anonymous": ANON_401, "user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, None),
    ("DELETE /api/conversations/{own}", "conv-delete-own", {"anonymous": ANON_401, "user": (204, None), "admin": (204, None)}, None),
    ("DELETE /api/conversations/{other user's}", "conv-delete-other", {"anonymous": ANON_401, "user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, None),
    ("GET /api/conversations/{own}/messages", "conv-msgs-own", {"anonymous": ANON_401, "user": (200, None), "admin": (200, None)}, None),
    ("GET /api/conversations/{other user's}/messages", "conv-msgs-other", {"anonymous": ANON_401, "user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, None),
    ("POST /api/chat {conversationId: other user's}", "chat-conv-other", {"user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, "none"),
    ("POST /api/chat/stream {conversationId: other user's}", "stream-conv-other", {"user": (404, "CONVERSATION_NOT_FOUND"), "admin": (404, "CONVERSATION_NOT_FOUND")}, "none"),
    ("GET /api/__test/admin (stand-in for a future Admin-policy endpoint)", "admin-only", {"anonymous": ANON_401, "user": (403, "AUTH_FORBIDDEN"), "admin": (200, None)}, None),
    ("GET /health (no health endpoint is mapped)", "health", {"anonymous": (404, None), "user": (404, None), "admin": (404, None)}, None),
]

ROLE_TEXT = {
    "anonymous": "ไม่ล็อกอิน",
    "user": "User ที่ล็อกอิน",
    "admin": "Admin ที่ล็อกอิน",
    "logged-out": "cookie ของ session ที่ logout แล้ว",
    "disabled": "บัญชีถูกปิดระหว่างล็อกอิน",
    "idle-expired": "session ไม่ได้ใช้งานครบ 30 นาที",
    "absolute-expired": "session ครบ 8 ชั่วโมง",
    "role-changed": "role ถูกเปลี่ยนระหว่างล็อกอิน",
}

for label, key, roles, ollama_mode in MATRIX:
    for role, (status, code) in roles.items():
        allowed = status < 300
        if ollama_mode == "chat":
            ollama = 1 if allowed else 0
        elif ollama_mode == "none":
            ollama = 0
        else:
            ollama = None
        what = "อนุญาต" if allowed else ("ปฏิเสธ 401" if status == 401 else f"ปฏิเสธ {status}")
        if key == "health":
            what = "ไม่มี endpoint (404) ไม่เผยรายละเอียดระบบ"
        add(f"QA2-M-{key}-{role}", "M", f"{label} โดย {ROLE_TEXT[role]}",
            f"{what}: HTTP {status}" + (f" {code}" if code else "") + (f"; fake Ollama รับ {ollama} คำขอ" if ollama is not None else ""),
            status, code, ollama,
            "BE-006 matrix" if key not in ("conv-get-other", "conv-patch-other", "conv-delete-other", "conv-msgs-other", "chat-conv-other", "stream-conv-other") else "BE-007 contract (เจ้าของเท่านั้น; Admin ไม่อ่านแชตผู้อื่น)")

add("QA2-M-chat-stream-401-is-json", "M", "POST /api/chat/stream โดยไม่ล็อกอิน ตอบ JSON ไม่ใช่ SSE",
    "401 AUTH_REQUIRED เป็น application/json ก่อนเริ่ม SSE ไม่มี event-stream", 401, "AUTH_REQUIRED", 0, "BE-006 matrix (Issue #22)")

# --------------------------------------------------------------------------- A: authentication
A = "BE-005 contract"
add("QA2-A-01-login-ok", "A", "login ถูกต้อง", "200; ตัว body มีเฉพาะ user{id,displayName,username,roles}; ไม่มีรหัสผ่าน/hash/stamp/session; Cache-Control no-store", 200, None, None, A)
add("QA2-A-02-wrong-password", "A", "รหัสผ่านผิด (ชื่อมีอยู่)", "401 AUTH_INVALID_CREDENTIALS", 401, "AUTH_INVALID_CREDENTIALS", None, A)
add("QA2-A-03-unknown-user", "A", "ไม่มีชื่อผู้ใช้นี้", "401 AUTH_INVALID_CREDENTIALS เหมือนรหัสผ่านผิดทุกไบต์ (body เท่ากัน)", 401, "AUTH_INVALID_CREDENTIALS", None, A)
add("QA2-A-04-disabled", "A", "บัญชีถูกปิด (รหัสถูก)", "401 AUTH_INVALID_CREDENTIALS body เท่ากับกรณีรหัสผิด", 401, "AUTH_INVALID_CREDENTIALS", None, A)
add("QA2-A-05-locked", "A", "บัญชีถูกล็อก (รหัสถูก)", "401 AUTH_INVALID_CREDENTIALS body เท่ากับกรณีรหัสผิด", 401, "AUTH_INVALID_CREDENTIALS", None, A)
add("QA2-A-06-no-cookie-on-failure", "A", "login ล้มเหลวทุกแบบไม่ออก cookie session", "ไม่มี Set-Cookie ของ __Host-BC.LocalAI ที่มีค่า (ยกเว้นการลบ)", None, None, None, A)
add("QA2-A-07-no-echo", "A", "ข้อความผิดพลาดไม่สะท้อนชื่อผู้ใช้หรือรหัสผ่าน", "body ของ 401 ไม่มีชื่อผู้ใช้/รหัสผ่านที่ส่งไป", None, None, None, A)
add("QA2-A-08-same-headers", "A", "header ของ login ล้มเหลวเหมือนกันทุกสาเหตุ", "ชุดชื่อ header และสถานะเท่ากัน (ไม่นับ Date)", None, None, None, A)
add("QA2-A-09-same-work", "A", "ชื่อที่ไม่มีก็ทำงาน KDF เท่ากัน (เวลาไม่ต่างจนเดาชื่อได้)", "เวลามัธยฐานของชื่อที่ไม่มี >= 50% ของรหัสผิดชื่อที่มี", None, None, None, "BE-005 (KDF ทุกกรณี)")
add("QA2-A-10-no-csrf-token", "A", "login ไม่มี CSRF token", "403 AUTH_CSRF_INVALID", 403, "AUTH_CSRF_INVALID", 0, A)
add("QA2-A-11-bad-csrf-token", "A", "login ด้วย token ปลอม", "403 AUTH_CSRF_INVALID", 403, "AUTH_CSRF_INVALID", 0, A)
add("QA2-A-12-old-csrf-token", "A", "login ซ้ำด้วย token เดิมหลัง login แล้ว", "403 AUTH_CSRF_INVALID", 403, "AUTH_CSRF_INVALID", 0, A)
add("QA2-A-13-cross-origin", "A", "login จาก Origin ต่างเว็บ", "403 AUTH_CSRF_INVALID", 403, "AUTH_CSRF_INVALID", 0, A)
add("QA2-A-14-cross-site-fetch-metadata", "A", "login ที่ Sec-Fetch-Site: cross-site", "403 AUTH_CSRF_INVALID", 403, "AUTH_CSRF_INVALID", 0, A)
add("QA2-A-15-http", "A", "login ผ่าน HTTP (ไม่ใช่ HTTPS)", "403 AUTH_HTTPS_REQUIRED ไม่รับรหัสผ่านทางข้อความล้วน", 403, "AUTH_HTTPS_REQUIRED", 0, A)
for n, (v, title) in enumerate([
    ("not-json", "Content-Type ไม่ใช่ JSON"), ("extra-role", "มีฟิลด์ role แถม (mass assignment)"),
    ("duplicate", "ฟิลด์ซ้ำ"), ("wrong-type", "ชนิดข้อมูลผิด"), ("sql", "ชื่อผู้ใช้เป็น SQL"), ("empty", "ช่องว่าง")], 16):
    add(f"QA2-A-{n}-bad-input-{v}", "A", f"อินพุตผิดรูป: {title}", "400 AUTH_INVALID_REQUEST; ไม่เรียก backend; ไม่ออก cookie", 400, "AUTH_INVALID_REQUEST", 0, A)
add("QA2-A-22-oversized", "A", "body ใหญ่เกิน 16 KB", "413 REQUEST_TOO_LARGE", 413, "REQUEST_TOO_LARGE", 0, A)
add("QA2-A-23-rate-limit-account", "A", "ยิงรหัสผิดชื่อเดียวเกิน 5 ครั้ง/นาที", "ครั้งที่ 1-5 ได้ 401; ครั้งที่ 6 ได้ 429 AUTH_RATE_LIMITED พร้อม Retry-After", 429, "AUTH_RATE_LIMITED", None, A)
add("QA2-A-24-rate-limit-reset", "A", "หลังครบ 1 นาที ยิงได้ใหม่", "คำขอถัดไปไม่ใช่ 429 (ได้ 401 ตามปกติ)", 401, "AUTH_INVALID_CREDENTIALS", None, A)
add("QA2-A-25-rate-limit-same-body", "A", "429 ของชื่อที่มีกับชื่อที่ไม่มี เหมือนกัน", "body เท่ากัน ไม่เผยว่าชื่อมีอยู่", 429, "AUTH_RATE_LIMITED", None, A)
add("QA2-A-26-rate-limit-ip", "A", "ยิงหลายชื่อจาก IP เดียว เกิน 30 ครั้ง/นาที", "ครั้งที่ 31 ได้ 429 AUTH_RATE_LIMITED", 429, "AUTH_RATE_LIMITED", None, A)
add("QA2-A-27-lockout", "A", "รหัสผิด 5 ครั้ง (ห่างกันเกินกรอบอัตรา) แล้วใส่รหัสถูก", "ครั้งที่ 6 ที่รหัสถูก ได้ 401 AUTH_INVALID_CREDENTIALS เหมือนรหัสผิด (บัญชีถูกล็อก 15 นาที)", 401, "AUTH_INVALID_CREDENTIALS", None, A + " (ตัวแทน SQL แบบจำลอง)")
add("QA2-A-28-lockout-expires", "A", "ล็อกครบ 15 นาที", "login ด้วยรหัสถูกสำเร็จ 200", 200, None, None, A)
add("QA2-A-29-lockout-keeps-session", "A", "บัญชีถูกล็อกจากรหัสผิด ขณะเจ้าของล็อกอินอยู่", "เจ้าของยังใช้ /api/auth/me ได้ 200 (ไม่ถูกเตะออกด้วยการเดารหัส)", 200, None, None, "BE-006 matrix")
add("QA2-A-30-success-resets", "A", "รหัสผิด 4 ครั้ง ถูก 1 ครั้ง ผิดอีก 4 ครั้ง", "ไม่ล็อก: ล็อกอินด้วยรหัสถูกหลังจากนั้นสำเร็จ 200", 200, None, None, A + " (ตัวแทน SQL แบบจำลอง)")
add("QA2-A-31-username-case", "A", "ชื่อผู้ใช้ตัวพิมพ์ต่างกัน", "ใช้ได้ (normalized) 200", 200, None, None, "SEC-001")
add("QA2-A-32-password-exact", "A", "รหัสผ่านมีช่องว่างท้ายเพิ่ม", "401: รหัสต้องตรงทุกอักขระ ไม่ถูกตัดช่องว่าง", 401, "AUTH_INVALID_CREDENTIALS", None, "BE-005 (preserve exact password)")
add("QA2-A-33-db-down", "A", "ฐานข้อมูลผู้ใช้ล่มตอน login", "503 AUTH_UNAVAILABLE; ไม่มี cookie; body ไม่มีรายละเอียดภายใน", 503, "AUTH_UNAVAILABLE", None, A)
add("QA2-A-34-logout", "A", "logout (มี CSRF token)", "204; ลบ cookie; ใช้ cookie เดิมซ้ำได้ 401 ทั้ง /me และ /api/chat (Ollama 0)", 204, None, 0, "BE-006 matrix")
add("QA2-A-35-logout-twice", "A", "logout ซ้ำ", "204 ทั้งสองครั้ง (idempotent)", 204, None, None, A)
add("QA2-A-36-logout-no-csrf", "A", "logout ไม่มี CSRF token", "403 AUTH_CSRF_INVALID และ session ยังไม่ถูกเพิกถอน", 403, "AUTH_CSRF_INVALID", None, A)
add("QA2-A-37-me-shape", "A", "รูป response ของ /api/auth/me", "มีเฉพาะ user{id,displayName,username,roles}", 200, None, None, A)
add("QA2-A-38-relogin", "A", "login เป็นอีกคนบน browser เดิม", "session เดิมถูกเพิกถอน: cookie เก่าใช้ไม่ได้ 401", 401, "AUTH_REQUIRED", None, "BE-005 (re-login replaces session)")
add("QA2-A-39-concurrent-login-cap", "A", "login พร้อมกันเกินเพดาน 2", "ไม่มีคำขอใดได้ 500; ทุกคำตอบเป็น 200/401/429", None, None, None, A)

# --------------------------------------------------------------------------- S: session
S = "BE-006 matrix"
add("QA2-S-01-idle-29m", "S", "ไม่ใช้งาน 29 นาที 59 วินาที", "session ยังใช้ได้ 200", 200, None, None, S)
add("QA2-S-02-idle-30m", "S", "ไม่ใช้งานครบ 30 นาที", "401 AUTH_REQUIRED และ session ถูกเพิกถอน (ใช้ต่อไม่ได้แม้ย้อนเวลา)", 401, "AUTH_REQUIRED", 0, S)
add("QA2-S-03-activity-extends-idle", "S", "ใช้งานทุก 20 นาทีตลอด 7 ชั่วโมง", "ยังใช้ได้ 200 (idle ต่ออายุตามการใช้งาน)", 200, None, None, S)
add("QA2-S-04-absolute-8h", "S", "ใช้งานต่อเนื่องจนครบ 8 ชั่วโมง", "401 AUTH_REQUIRED (การต่อเนื่องไม่ขยายอายุ)", 401, "AUTH_REQUIRED", 0, S)
add("QA2-S-05-absolute-7h59", "S", "7 ชั่วโมง 59 นาทีที่ใช้งานต่อเนื่อง", "ยังใช้ได้ 200", 200, None, None, S)
add("QA2-S-06-disabled", "S", "ปิดบัญชีระหว่างล็อกอิน", "คำขอถัดไป /api/chat ได้ 401 AUTH_REQUIRED Ollama 0", 401, "AUTH_REQUIRED", 0, S)
add("QA2-S-07-reenabled-stays-dead", "S", "เปิดบัญชีคืนหลังถูกปิด", "session เก่ายังใช้ไม่ได้ 401 (ต้อง login ใหม่)", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-08-role-up", "S", "เปลี่ยน User เป็น Admin ระหว่างล็อกอิน", "session เก่า 401; login ใหม่เข้า endpoint Admin ได้ 200", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-09-role-down", "S", "เปลี่ยน Admin เป็น User ระหว่างล็อกอิน", "endpoint Admin ได้ 401 ทันที (ไม่ใช้สิทธิ์ค้างเดิม) และไม่ได้ 200", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-10-stamp", "S", "เปลี่ยน security stamp (เปลี่ยนรหัส/สิทธิ์)", "session เก่า 401", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-11-deleted", "S", "ลบบัญชีระหว่างล็อกอิน", "401", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-12-store-down", "S", "ฐานข้อมูลผู้ใช้ติดต่อไม่ได้ระหว่างใช้งาน", "ปฏิเสธ 401 (fail closed ไม่ใช่ 500); ฐานกลับมาแล้ว session เดิมใช้ต่อได้ 200", 401, "AUTH_REQUIRED", 0, S)
add("QA2-S-13-revoke-all", "S", "เพิกถอนทุก session ของผู้ใช้ (2 เครื่อง)", "ทั้งสอง cookie 401; ผู้ใช้อื่นไม่กระทบ 200", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-14-unknown-role", "S", "บัญชีมี role ที่ไม่รู้จัก (KnowledgeManager)", "session ที่มีอยู่ 401", 401, "AUTH_REQUIRED", None, S)
add("QA2-S-15-cookie-flags", "S", "flag ของ cookie session", "ชื่อ __Host-BC.LocalAI; Secure; HttpOnly; SameSite=Lax; Path=/; ไม่มี Domain", None, None, None, "BE-006")
add("QA2-S-16-csrf-cookie-flags", "S", "flag ของ cookie CSRF", "ชื่อ __Host-BC.Csrf; Secure; HttpOnly; SameSite=Strict; Path=/; ไม่มี Domain", None, None, None, "BE-005")
add("QA2-S-17-session-cookie", "S", "cookie ไม่คงอยู่เกินการปิดเบราว์เซอร์", "ไม่มี Expires/Max-Age (session cookie)", None, None, None, "BE-006 (IsPersistent=false)")
add("QA2-S-18-no-reissue", "S", "คำขอที่ล็อกอินแล้วไม่ออก cookie ใหม่/ต่ออายุ cookie", "ไม่มี Set-Cookie ของ session ใน response ของ /api/auth/me", None, None, None, "BE-006 (SlidingExpiration=false)")
add("QA2-S-19-fixation-garbage", "S", "session fixation: ฝัง cookie session ปลอมก่อน login", "login ออก cookie ใหม่ที่ไม่ใช่ค่าที่ฝัง; ค่าที่ฝังไม่เคยกลายเป็น session ที่ใช้ได้ (401)", 401, "AUTH_REQUIRED", None, "BE-006 (session fixation)")
add("QA2-S-20-fixation-valid-cookie", "S", "session fixation: attacker ถือ cookie ที่ valid ของตนแล้วเหยื่อล็อกอินบน browser เดียวกัน", "cookie เดิมถูกเพิกถอนเมื่อมี login ใหม่ ใช้ต่อ 401", 401, "AUTH_REQUIRED", None, "BE-006 (session fixation)")
add("QA2-S-21-tampered-cookie", "S", "cookie ถูกแก้ (ตัดท้าย / สลับตัวอักษร / ว่าง / ยาวมาก)", "401 ไม่ใช่ 500 ทุกรูปแบบ", 401, "AUTH_REQUIRED", 0, "BE-006")
add("QA2-S-22-cookie-opaque", "S", "ค่า cookie ไม่เปิดเผยข้อมูล", "ค่า cookie ไม่มี user id, security stamp หรือ session id แบบข้อความล้วน", None, None, None, "BE-006 (cookie ไม่มีความลับ)")
add("QA2-S-23-two-users-isolated", "S", "สองผู้ใช้ล็อกอินพร้อมกัน", "logout ของคนหนึ่งไม่กระทบอีกคน (200)", 200, None, None, S)

# --------------------------------------------------------------------------- I: authorization / IDOR
I = "BE-007 contract"
add("QA2-I-01-list-own-only", "I", "รายการสนทนาแสดงเฉพาะของตัวเอง", "Alice เห็นเฉพาะของ Alice; Bob เห็นเฉพาะของ Bob", 200, None, None, I)
add("QA2-I-02-other-same-as-missing", "I", "เปิดสนทนาของคนอื่น กับ id ที่ไม่มีอยู่จริง", "404 CONVERSATION_NOT_FOUND และ body เหมือนกันทุกไบต์ (ถามเดา id ไม่ได้)", 404, "CONVERSATION_NOT_FOUND", None, I)
add("QA2-I-03-other-unchanged", "I", "แก้/ลบ/อ่านสนทนาของคนอื่นแล้วข้อมูลเจ้าของไม่เปลี่ยน", "ชื่อ ข้อความ และตัวสนทนาของเจ้าของไม่เปลี่ยนหลังความพยายามของคนอื่น", None, None, None, I)
add("QA2-I-04-chat-other-conv-no-write", "I", "ส่งแชตเข้าสนทนาของคนอื่น", "404 และไม่มีข้อความเพิ่มในสนทนาของเจ้าของ; Ollama 0", 404, "CONVERSATION_NOT_FOUND", 0, I)
add("QA2-I-05-admin-no-read", "I", "Admin อ่าน/แก้/ลบ/ส่งแชตเข้าสนทนาของ User", "404 ทุกกรณี (Admin ไม่มีสิทธิ์อ่านแชตผู้อื่น)", 404, "CONVERSATION_NOT_FOUND", 0, "SEC-001 decision")
add("QA2-I-06-invalid-ids", "I", "id รูปแบบผิด (ไม่ใช่ GUID, path traversal, SQL, GUID รูปแบบอื่น, ยาวมาก)", "400 INVALID_ID ทุกรูปแบบ ไม่ใช่ 500", 400, "INVALID_ID", None, I)
add("QA2-I-07-body-owner-ignored", "I", "สร้างสนทนาพร้อมฟิลด์ userId/ownerId ของคนอื่นใน body", "สนทนาเป็นของผู้เรียก ไม่ใช่ของ id ที่ใส่มา", 201, None, None, I + " (เจ้าของมาจาก session)")
add("QA2-I-08-header-query-ignored", "I", "ใส่ ?userId= / X-User-Id / X-Forwarded-User ของคนอื่น", "ถูกเมิน: ผู้เรียกเห็นเฉพาะของตัวเอง", 200, None, None, I)
add("QA2-I-09-cursor-cross-user", "I", "ใช้ cursor ของ Alice กับบัญชี Bob", "ไม่ได้ข้อมูลของ Alice (ได้ของ Bob หรือ 400)", None, None, None, I)
add("QA2-I-10-deleted-404", "I", "สนทนาที่ลบแล้ว", "เจ้าของเปิดได้ 404 CONVERSATION_NOT_FOUND", 404, "CONVERSATION_NOT_FOUND", None, I)
add("QA2-I-11-no-owner-in-dto", "I", "DTO ไม่มีฟิลด์เจ้าของ", "response ของ conversation/message ไม่มี userId/ownerId/ลำดับภายใน", None, None, None, I)
add("QA2-I-12-patch-mass-assign", "I", "PATCH ที่มีฟิลด์อื่น (userId, id, createdAt)", "เปลี่ยนเฉพาะ title; id/เจ้าของ/เวลาสร้างไม่เปลี่ยน", 200, None, None, I)
add("QA2-I-13-user-admin-endpoint", "I", "User เรียก endpoint ระดับ Admin", "403 AUTH_FORBIDDEN ไม่มีข้อมูล", 403, "AUTH_FORBIDDEN", None, S)
add("QA2-I-14-forged-role-cookie", "I", "ยกระดับสิทธิ์ด้วย cookie ที่แก้ role", "401 (ลายเซ็น/การเข้ารหัสของ cookie กันไว้) ไม่ใช่ 200/403", 401, "AUTH_REQUIRED", None, S)
add("QA2-I-15-login-role-from-account", "I", "role มาจากบัญชีฝั่ง server ไม่ใช่จากผู้เรียก", "ผู้ใช้ role User ได้ roles=[User] เสมอ", 200, None, None, S)

# --------------------------------------------------------------------------- C: CSRF
C = "BE-006 (SameSite guard) + BE-005 (antiforgery)"
CSRF_ENDPOINTS = ["chat", "stream", "conv-create", "conv-patch", "conv-delete", "logout"]
CSRF_VARIANTS = [("sfs-cross-site", "Sec-Fetch-Site: cross-site"), ("sfs-same-site", "Sec-Fetch-Site: same-site (เว็บพี่น้อง)"),
                 ("origin-evil", "Origin: https://evil.example"), ("origin-null", "Origin: null (sandbox/ฟอร์มข้ามเว็บ)"),
                 ("origin-http-same-host", "Origin: http://localhost (scheme ต่าง)"), ("origin-double", "Origin ซ้ำสองค่า")]
for ep in CSRF_ENDPOINTS:
    for v, vt in CSRF_VARIANTS:
        add(f"QA2-C-{ep}-{v}", "C", f"{ep}: {vt}", "403 AUTH_CSRF_INVALID; ไม่มีผลข้างเคียง (Ollama 0, ข้อมูลเดิม, session ยังอยู่)", 403, "AUTH_CSRF_INVALID", 0, C)
add("QA2-C-allow-same-origin", "C", "Origin เดียวกัน + Sec-Fetch-Site: same-origin (เหมือน browser)", "chat/stream/สร้าง/แก้/ลบ สำเร็จ", 200, None, None, C)
add("QA2-C-allow-sfs-none", "C", "Sec-Fetch-Site: none (ผู้ใช้พิมพ์ URL)", "ไม่ถูกบล็อกโดย guard", 200, None, None, C)
add("QA2-C-allow-no-browser-headers", "C", "ไม่มี Origin/Sec-Fetch (สคริปต์/เครื่องมือ)", "ไม่ถูกบล็อก 200 (CSRF ต้องมีเบราว์เซอร์; ออกแบบไว้)", 200, None, None, C)
add("QA2-C-simple-text-plain", "C", "ฟอร์มข้ามเว็บแบบ text/plain ที่ไม่มี header เบราว์เซอร์", "415 และ Ollama 0 (ไม่รับ body ที่ไม่ใช่ JSON)", 415, None, 0, "ชั้นป้องกันที่สอง (JSON-only)")
add("QA2-C-simple-form-urlencoded", "C", "ฟอร์ม application/x-www-form-urlencoded", "415 และ Ollama 0", 415, None, 0, "ชั้นป้องกันที่สอง (JSON-only)")
add("QA2-C-cors-none", "C", "CORS: preflight และ response ข้ามเว็บ", "ไม่มี Access-Control-Allow-Origin/Credentials ในทุก response", None, None, None, "QA baseline")
add("QA2-C-stream-fetch-no-token", "C", "fetch POST ของหน้าเว็บ (same-origin) ไปที่ /api/chat/stream แล้วอ่านสตรีม", "200 text/event-stream; started, delta, completed ตามลำดับ", 200, None, 1, "Issue QA-002 3.7")
add("QA2-C-stream-fetch-with-token", "C", "เหมือนกัน แต่แนบ X-CSRF-TOKEN จาก /api/auth/csrf", "200 สตรีมสำเร็จเหมือนกัน (token ที่แนบเกินจำเป็นไม่ทำให้พัง)", 200, None, 1, "Issue QA-002 3.7")
add("QA2-C-method-override", "C", "X-HTTP-Method-Override: DELETE บน GET", "ไม่ลบอะไร (header ถูกเมิน)", 200, None, None, "QA baseline")
add("QA2-C-get-safe", "C", "GET ไม่เปลี่ยนสถานะ", "เรียก GET ทุก endpoint ที่มี cookie แล้วข้อมูลไม่เปลี่ยน", 200, None, None, "QA baseline")

# --------------------------------------------------------------------------- H: security headers
add("QA2-H-01-home", "H", "GET / (หน้าแชต)", "CSP มี default-src 'self', object-src 'none', frame-ancestors 'none'; X-Content-Type-Options nosniff; Referrer-Policy no-referrer", 200, None, None, "FE-001")
add("QA2-H-02-auth-api", "H", "/api/auth/* ทุก endpoint", "Cache-Control no-store, Pragma no-cache, nosniff", None, None, None, "BE-005")
add("QA2-H-03-401-no-store", "H", "401 AUTH_REQUIRED จาก /api/chat", "Cache-Control no-store", 401, "AUTH_REQUIRED", None, "BE-006")
add("QA2-H-04-403-no-store", "H", "403 AUTH_FORBIDDEN และ AUTH_CSRF_INVALID", "Cache-Control no-store", 403, None, None, "BE-006")
add("QA2-H-05-conversations-no-store", "H", "response ของ /api/conversations", "Cache-Control no-store", 200, None, None, "BE-007")
add("QA2-H-06-chat-no-store", "H", "200 ของ /api/chat (คำตอบส่วนตัวของผู้ใช้)", "Cache-Control no-store (กัน proxy/เบราว์เซอร์เก็บคำตอบ)", 200, None, None, "QA baseline")
add("QA2-H-07-stream-headers", "H", "/api/chat/stream", "text/event-stream; Cache-Control no-cache หรือ no-store; X-Accel-Buffering no", 200, None, None, "BE-004")
add("QA2-H-08-hsts", "H", "HSTS ใน Production ผ่าน HTTPS (ชื่อโฮสต์ที่ไม่ใช่ localhost)", "มี Strict-Transport-Security", 200, None, None, "QA baseline")
add("QA2-H-09-no-powered-by", "H", "ไม่เปิดเผยเทคโนโลยี", "ไม่มี X-Powered-By / X-AspNet-Version", None, None, None, "QA baseline")
add("QA2-H-10-html-pages-frame", "H", "หน้า HTML อื่นนอกจากหน้าแชต (Privacy)", "กัน framing: มี X-Frame-Options หรือ CSP frame-ancestors", 200, None, None, "QA baseline")
add("QA2-H-11-api-nosniff", "H", "ทุก response ของ /api (chat, stream, conversations, 401/403)", "X-Content-Type-Options: nosniff", None, None, None, "QA baseline")
add("QA2-H-12-error-json", "H", "404/405/415 ใต้ /api", "เป็น JSON หรือว่าง ไม่มีหน้า error HTML ของ framework", None, None, None, "QA baseline")

# --------------------------------------------------------------------------- L: leaks
add("QA2-L-01-ollama-500", "L", "Ollama ตอบ 500 พร้อมข้อความภายใน (พอร์ต เส้นทางเครื่อง) ผ่าน /api/chat", "502 AI_PROVIDER_ERROR; body และ log ไม่มี canary/พอร์ต/host/เส้นทาง", 502, "AI_PROVIDER_ERROR", 1, "AI-005")
add("QA2-L-02-ollama-500-stream", "L", "เหมือนกันผ่าน /api/chat/stream", "event: error AI_PROVIDER_ERROR; ไม่มีรายละเอียดภายใน", 200, "AI_PROVIDER_ERROR", 1, "AI-005")
add("QA2-L-03-ollama-down", "L", "Ollama ไม่ทำงาน (connection refused) ทั้งสอง endpoint", "503 AI_UNAVAILABLE / event error AI_UNAVAILABLE; ไม่มี host:port", 503, "AI_UNAVAILABLE", None, "AI-005")
add("QA2-L-04-ollama-garbage", "L", "Ollama ตอบ body เสีย", "AI_INVALID_RESPONSE; ไม่มีเนื้อหา body เดิม", 502, "AI_INVALID_RESPONSE", None, "AI-005")
add("QA2-L-05-userstore-exception", "L", "ฐานข้อมูลผู้ใช้โยน exception ที่มี connection string", "401 AUTH_REQUIRED ทั่วไป; body และ log ไม่มี canary", 401, "AUTH_REQUIRED", 0, "BE-006 (SecurityLog ไม่ใส่ข้อความ exception)")
add("QA2-L-06-history-exception", "L", "ที่เก็บประวัติโยน exception ที่มี connection string (เป็น ChatHistoryUnavailable)", "503 HISTORY_UNAVAILABLE; body และ log ไม่มี canary", 503, "HISTORY_UNAVAILABLE", None, "BE-007")
add("QA2-L-07-unhandled-exception", "L", "exception ที่ไม่คาดคิดจาก dependency (มีความลับในข้อความ)", "500 INTERNAL_ERROR ทั่วไป; log ไม่มีความลับ", 500, "INTERNAL_ERROR", None, "AGENTS.md ข้อ 12 (ไม่ log ความลับ)")
add("QA2-L-08-login-failure-logs", "L", "log หลัง login ล้มเหลวหลายแบบ", "log ไม่มีรหัสผ่านที่ส่งมา hash หรือ stamp", None, None, None, "BE-005")
add("QA2-L-09-sweep", "L", "กวาด log ทั้งหมดหลัง login/chat/stream/logout/ปฏิเสธต่าง ๆ", "log ไม่มีรหัสผ่าน hash stamp session id ค่า cookie หรือ CSRF token", None, None, None, "BE-006 SecurityLog")
add("QA2-L-10-audit-events", "L", "เหตุการณ์ตรวจสอบ", "มี log 5001 (สร้าง session), 5002 (logout), 5003 (ปฏิเสธพร้อมเหตุผล), 5005 (ข้ามเว็บ) และไม่มีค่าลับ", None, None, None, "BE-006 SecurityLog")
add("QA2-L-11-framework-errors", "L", "อินพุตผิด (JSON เสีย/ชนิดผิด/ใหญ่เกิน) ที่ /api", "400/413/415 ไม่มี stack trace, ชื่อชนิด .NET หรือเส้นทางไฟล์", 400, "INVALID_MESSAGE", 0, "AGENTS.md ข้อ 12")
add("QA2-L-12-same-reason-hidden", "L", "401 ของ session ที่ logout / หมดอายุ / ปิดบัญชี / เปลี่ยน role", "body เหมือนกันทุกไบต์ (ไม่บอกเหตุผล)", 401, "AUTH_REQUIRED", None, "BE-006 matrix")

# --------------------------------------------------------------------------- U: never reaches Ollama
add("QA2-U-01-anon-chat-x10", "U", "ไม่ล็อกอิน เรียก /api/chat 10 ครั้ง", "401 ทุกครั้ง; Ollama 0", 401, "AUTH_REQUIRED", 0, "Issue QA-002 3.6")
add("QA2-U-02-anon-stream-x10", "U", "ไม่ล็อกอิน เรียก /api/chat/stream 10 ครั้ง", "401 JSON ทุกครั้ง; Ollama 0", 401, "AUTH_REQUIRED", 0, "Issue QA-002 3.6")
add("QA2-U-03-anon-invalid-body", "U", "ไม่ล็อกอิน ส่ง body ว่าง/เสีย", "401 (ตรวจสิทธิ์ก่อนตรวจ body) Ollama 0", 401, "AUTH_REQUIRED", 0, "Issue QA-002 3.6")
add("QA2-U-04-anon-oversized", "U", "ไม่ล็อกอิน ส่ง body 2 MB", "ไม่ใช่ 200; Ollama 0 (401 หรือ 413)", None, None, 0, "Issue QA-002 3.6")
add("QA2-U-05-anon-conversation-id", "U", "ไม่ล็อกอิน ระบุ conversationId", "401; Ollama 0", 401, "AUTH_REQUIRED", 0, "Issue QA-002 3.6")
add("QA2-U-06-rejected-never-reach", "U", "คำขอที่ถูกปฏิเสธทุกแบบ (CSRF, session สิ้นสุด, ปิดบัญชี, 415)", "Ollama 0 หลังชุดคำขอทั้งหมด", None, None, 0, "Issue QA-002 3.6")
add("QA2-U-07-invalid-message", "U", "ผู้ใช้ล็อกอิน ส่งข้อความว่าง", "400 INVALID_MESSAGE; Ollama 0", 400, "INVALID_MESSAGE", 0, "BE-001")
add("QA2-U-08-counter-sanity", "U", "ตรวจตัวนับของ fake Ollama (คำขอถูกต้องของผู้ใช้)", "chat 1 ครั้ง stream 1 ครั้ง นับได้ 1 ต่อคำขอ", 200, None, 1, "ตรวจเครื่องมือวัด")
add("QA2-U-09-http-scheme", "U", "เรียก /api/chat ผ่าน HTTP", "ไม่ใช่ 200; Ollama 0 (redirect หรือ 401)", None, None, 0, "BE-005")

# --------------------------------------------------------------------------- MS: session ended during a stream
MS = "Issue QA-002 3.7"
for key, title in [("logout", "logout จากอีกแท็บระหว่างสตรีม"), ("revoke-all", "เพิกถอนทุก session ระหว่างสตรีม"),
                   ("disabled", "ปิดบัญชีระหว่างสตรีม (ตรวจบัญชีทุก 5 วินาที)"), ("role", "เปลี่ยน role ระหว่างสตรีม"),
                   ("stamp", "เปลี่ยน security stamp ระหว่างสตรีม"), ("absolute", "ครบ 8 ชั่วโมงระหว่างสตรีม")]:
    add(f"QA2-MS-{key}", "MS", title,
        "event ลำดับ started, delta (เท่าที่ส่งก่อนหน้า), error AUTH_SESSION_ENDED เพียงครั้งเดียว; ไม่มี completed; ข้อความหลังเพิกถอนไม่ถึงผู้ใช้; การเชื่อมต่อ Ollama ถูกปิด",
        200, "AUTH_SESSION_ENDED", 1, MS)
add("QA2-MS-reuse", "MS", "ใช้ cookie เดิมหลังสตรีมถูกตัด", "401 และ Ollama ไม่ถูกเรียกเพิ่ม", 401, "AUTH_REQUIRED", 1, MS)
add("QA2-MS-store-down", "MS", "ฐานข้อมูลผู้ใช้ล่มระหว่างสตรีม", "สตรีมจบด้วย error AUTH_SESSION_ENDED (fail closed); session ไม่ถูกเพิกถอน ฐานกลับมาแล้วใช้ต่อได้", 200, "AUTH_SESSION_ENDED", 1, MS)
add("QA2-MS-long-active", "MS", "สตรีมยาว 40 นาทีที่ยังส่งข้อความอยู่", "จบ completed (ไม่ถูกตัดด้วย idle 30 นาที)", 200, None, 1, MS)
add("QA2-MS-stalled-model", "MS", "เพิกถอน session ขณะโมเดลเงียบ (กำลังคิด ไม่ส่งข้อความใหม่)", "สตรีมจบด้วย error AUTH_SESSION_ENDED ภายใน 10 วินาทีหลังเพิกถอน แม้โมเดลไม่ส่งข้อความเพิ่ม", 200, "AUTH_SESSION_ENDED", 1, MS)
add("QA2-MS-history", "MS", "สตรีมในสนทนา แล้ว session สิ้นสุด", "ข้อความตอบถูกบันทึกเป็นสถานะ failed รหัส AUTH_SESSION_ENDED พร้อมข้อความที่ส่งไปแล้ว ไม่ใช่ completed", 200, "AUTH_SESSION_ENDED", 1, "BE-007 + " + MS)
add("QA2-MS-chat", "MS", "session สิ้นสุดระหว่างรอ /api/chat", "401 AUTH_SESSION_ENDED ไม่ส่งคำตอบ", 401, "AUTH_SESSION_ENDED", 1, MS)
add("QA2-MS-other-user", "MS", "เพิกถอน session ของคนอื่นระหว่างสตรีมของ Alice", "สตรีมของ Alice จบ completed", 200, None, 1, MS)
add("QA2-MS-wire-format", "MS", "รูป event ปิดท้ายหลังเพิกถอน", "เป็น SSE ถูกต้อง: event: error, data เป็น JSON มี code/message/partial", 200, "AUTH_SESSION_ENDED", 1, "BE-004 SSE contract")

# --------------------------------------------------------------------------- Q: flooding / queue
add("QA2-Q-01-one-user-many", "Q", "ผู้ใช้หนึ่งคนยิงสตรีม 6 คำขอพร้อมกัน ขณะผู้ใช้อื่น 1 คำขอ (Ollama จำลองทำทีละงาน)", "ทุกคำขอจบ completed หรือ error ที่มี code ตามสัญญา; ไม่มี 500/INTERNAL_ERROR; ผู้ใช้อื่นไม่ค้างเกิน 30 วินาที", 200, None, 7, "Issue QA-002 3.6")
add("QA2-Q-02-timeout-message", "Q", "คิวยาวจนเลย timeout ของ Ollama", "ผู้ใช้ที่ช้าได้ event error code AI_TIMEOUT พร้อมข้อความที่อ่านรู้เรื่อง ไม่ใช่การตัดการเชื่อมต่อเงียบ ๆ", 200, "AI_TIMEOUT", None, "Issue QA-002 3.6 / BE-003")
add("QA2-Q-03-per-user-cap", "Q", "มีเพดานคำขอพร้อมกันต่อผู้ใช้", "ผู้ใช้เดียวไม่สามารถยึดคิวทั้งหมด: อย่างน้อยบางคำขอส่วนเกินถูกปฏิเสธด้วย code ที่เข้าใจได้ (เช่น 429) (QA baseline; ถ้าไม่มีให้บันทึกเป็นข้อสังเกต)", 429, None, None, "QA baseline")

# --------------------------------------------------------------------------- O: auth off / configuration
add("QA2-O-01-auth-off-routes", "O", "Authentication:Enabled=false: /api/auth/*", "404 ทั้ง csrf และ login (ไม่เปิดเผยว่ามี)", 404, None, 0, "BE-005")
add("QA2-O-02-auth-off-chat-open", "O", "Authentication:Enabled=false: /api/chat และ /stream โดยไม่ล็อกอิน", "200 (ค่าเริ่มต้นเปิดโล่งตามที่ระบุไว้ใน BE-006)", 200, None, 2, "BE-006 matrix (ค่าเริ่มต้น)")
add("QA2-O-03-auth-off-admin", "O", "Authentication:Enabled=false: endpoint Admin โดยไม่ล็อกอิน", "ยังต้องล็อกอิน: 401", 401, "AUTH_REQUIRED", None, "BE-006 matrix")
add("QA2-O-04-history-needs-auth", "O", "ChatHistory:Enabled=true แต่ Authentication:Enabled=false", "แอปไม่เริ่ม (ตรวจ options ตอนเริ่ม ล้มอย่างปิด)", None, None, None, "BE-007")
add("QA2-O-05-history-off-404", "O", "ChatHistory:Enabled=false (ค่าเริ่มต้น): /api/conversations", "404 ก่อนตรวจสิทธิ์ ทั้งผู้ไม่ล็อกอินและผู้ล็อกอิน", 404, None, 0, "BE-007")
add("QA2-O-06-history-off-chat-conv", "O", "ChatHistory ปิด แต่ส่ง conversationId", "404 CONVERSATION_NOT_FOUND; Ollama 0", 404, "CONVERSATION_NOT_FOUND", 0, "BE-007")
add("QA2-O-07-production-default-open", "O", "ค่าเริ่มต้นใน appsettings.json", "บันทึกเป็นข้อสังเกต: Authentication.Enabled=false เป็นค่าเริ่มต้น จึงต้องเปิดก่อนใช้กับผู้ใช้จริง", None, None, None, "BE-006 matrix")


def render() -> str:
    ids = [c["id"] for c in CASES]
    if len(ids) != len(set(ids)):
        dup = sorted({i for i in ids if ids.count(i) > 1})
        raise SystemExit(f"duplicate case ids: {dup}")
    return json.dumps({"groups": GROUPS, "cases": CASES}, ensure_ascii=False, indent=1) + "\n"


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    text = render()
    if "--check" in argv:
        current = JSON_PATH.read_text(encoding="utf-8") if JSON_PATH.exists() else ""
        if current != text:
            print("qa002-cases.json is out of date; run: python tests/security/qa002_cases.py", file=sys.stderr)
            return 1
        return 0
    JSON_PATH.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {JSON_PATH.name}: {len(CASES)} cases")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
