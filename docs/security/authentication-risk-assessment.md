# SEC-001 — Risk Assessment / Threat Model (Proposed)

วันที่: 2026-10-20 · **ไม่ใช่ผลพบช่องโหว่ใน Legacy ที่ยังไม่มี Source ให้ตรวจ**

Assets: password hashes, identity/role, session, chat ownership, DB credentials, Data Protection keys
Attackers: anonymous browser, compromised employee account, hostile AI text, local unprivileged OS user
Boundaries: browser→API, service→MySQL, server session→cookie, API→Ollama, local secret directory→Git/logs

| ID | Threat / severity | Mitigation / verification | สถานะ/ผู้รับผิดชอบ |
|---|---|---|---|
| R01 | Legacy algorithm/encoding unknown — High | — | **N/A**: ไม่มีระบบ Legacy (การตัดสินใจของเจ้าของโปรเจกต์ 2026-10-20) |
| R02 | ใช้บัญชีของ BC-LocalAI แทนบัญชีบริษัทโดยไม่ได้อนุมัติ — High | ได้คำสั่งให้ใช้ MySQL ของ BC-LocalAI | **ปิดแล้ว** (เจ้าของโปรเจกต์ตัดสิน); ที่เหลือคือการดูแลบัญชี = R14 |
| R03 | Offline password cracking — High | framework IdentityV3, random salt, 220k iterations สำหรับ fixture; benchmark policy ก่อนจริง | Local tests; BE-005 production pending |
| R04 | SQL injection / excessive DB grants — High | parameterized values, read-only user lookup, schema-specific grants, no root in app | Reader grant tested locally; API integration pending |
| R05 | Enumeration / brute force / hash CPU exhaustion — High | generic errors, dummy hash, bounded rate/concurrency, reviewed per-user/IP lockout | BE-005 not implemented |
| R06 | Login/logout CSRF — High | antiforgery bootstrap/token header, same-origin, safe cookie, no GET mutation | BE-005/006 not implemented |
| R07 | Session theft/fixation/replay — High | Secure HttpOnly cookie, new session at Login, server revoke, Data Protection ACL | BE-006 not implemented |
| R08 | Disabled/deleted/role-changed account retains access — High | active/stamp checks, fail closed, cancel open SSE within approved bound | BE-006 pending policy |
| R09 | IDOR / forged role — High | server principal ID, ownership + policies on every resource, no body-supplied roles | BE-006/007 pending |
| R10 | Credentials or AI data in logs/Git — High | ignored .local, restricted Windows ACL, no printing secret/hash/raw errors | Local files protected; app logging tests still required later |
| R11 | MySQL exposed to network — High | new instance bind 127.0.0.1, port3307, mysqlx off; no firewall opening | Local verification; existing3306 service untouched/not audited |
| R12 | Unsafe AI HTML/XSS steals browser actions — High | text rendering/CSP, no unsafe innerHTML; HttpOnly alone does not stop CSRF-like actions from XSS | FE-002/FE-003 tests separate |
| R13 | Lost keys/DB/restart leaves orphan sessions — Medium | backup/restore, durable session store, defined expiration, fail closed on store loss | Production recovery policy pending |
| R14 | New account lifecycle has no owner/reset process — High | Admin role creates/disables/resets accounts through a script; audit of those actions | **Owner decided** (Admin via script); the script and its audit are BE-005/006 work |

## Risks that must not be accepted silently

R01 is N/A (no legacy system) and R02 is closed by the owner's decision. R05–R09/R14 still prevent using this preparation as production Authentication until BE-005/006 implement and test them.
Security sign-off owner: **watzapon (เติ้ฬ)**, delegated by the project owner (2026-10-20). Residual risk acceptance belongs to that person, not to the author/tool.
Sign-off of this design is recorded in PR #33; implementation (BE-005/006) needs its own review and sign-off.
Local root/admin credential is only for this datadir. Do not copy fixture credentials or private option files into the application.

## Conditional migration recommendation

If actual Legacy source later proves weak hashes, document exact evidence, require system-owner approval,
consider central verification/controlled rehash-on-login or forced reset under a separate approved migration.
Do not write to company DB or silently accept obsolete hashes here. No weak-hash finding is asserted without source.

Sources: [Microsoft Identity configuration](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0),
[OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html),
[local Architecture](authentication-architecture.md), [Test Plan](authentication-test-plan.md)
