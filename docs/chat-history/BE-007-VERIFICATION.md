# BE-007 — Preparation Verification / Handoff

วันที่ 2026-10-20 (Asia/Bangkok) · Issue [#23](https://github.com/b3amkawi/BC-LocalAI/issues/23)
Branch `watzapon_BE-007` · worktree `BC_BE007` · **Partial preparation, NOT the completed feature**

## 1. Baseline and authorized scope

ใช้ branch งานเดิมที่มี groundwork commit `1f0ac03` ไม่ลบทิ้งหรือสร้าง branch ชื่อซ้ำ
ตามคำสั่งผู้ใช้ pull `origin/main` ที่ `91916ea0daf71c22d2abb10e36f97088f3bea63a` ก่อนแก้โค้ด
merge ใน branch งาน: `258f23c95b333042e75f3aa8188aec258acde65c`
main มี BE-003/BE-004/FE-002 แล้ว; BE-005/BE-006 ยังเป็น Required dependency ก่อนใช้ History จริง
SEC-001 ถูกผู้ใช้สั่งหยุด ไม่ได้นำมา merge หรือแก้ Authentication เพื่อข้าม dependency

รอบนี้ทำ Schema/Migration/Rollback **Draft**, Repository Contract, offline business-rule/context prototype,
Tests และเอกสารส่งต่อ ตามส่วนที่ Issue อนุญาตให้เตรียมล่วงหน้า
ไม่มี History Controller, database provider dependency, fake/default owner, DI registration, AI contract change หรือ production deployment

## 2. Defects reproduced before fixes

Baseline filtered suite ผ่าน93/93 แต่ regression ใหม่12กรณี **fail12/12** (exit1) ก่อนแก้:

| Defect | Evidence / fix |
|---|---|
| Unknown current/next MessageStatus ผ่าน state validation | 4 failing cases → Enum.IsDefined guard |
| Reference repository รับ undefined MessageRole | failing case → common role/content validation before mutation |
| Context รับข้อความหลาย Conversation และ Sequence ซ้ำ | 2 failing cases → reject mixed/ambiguous metadata; ไม่ใช่ authorization substitute |
| Sum token cost int overflow / negative estimator | 2 failing cases → long accumulation + remaining-budget compare + negative rejection |
| Orphan assistant ถูกส่งเป็น context โดยไม่มีคำถาม | failing case → exclude orphan instead of inventing a turn |
| Null page กลายเป็น NullReference / cursor length ไม่ bounded | 2 failing cases → safe INVALID_PAGE validation and256-char bound |

จุดพบจาก Source เพิ่มเติม: DDL เดิมใช้ ai_ci ซึ่งขัดกับ exact-owner contract เปลี่ยน draft เป็น utf8mb4_0900_bin/NO PAD
และแก้คำอธิบาย UTF-8 heuristic ที่เคยรับประกันมากเกินหลักฐาน ตัวอย่างภาษาไทยเดียวไม่ใช่ upper bound สำหรับทุก prompt
เพิ่ม UTF-8 storage limit/invalid Unicode/error-code tests และทดสอบ conversation activity หลัง update ข้อความ

## 3. Executed checks

คำสั่งจาก worktree BE-007; ชุด .NET/JS ใช้ mocks ไม่ต้องมี Ollama/MySQL

| Command / check | Actual result |
|---|---|
| Baseline `dotnet test ... --filter FullyQualifiedName~ChatHistory` | 93 passed |
| New regression before fix `--filter FullyQualifiedName~ChatHistoryPreparationRegressionTests` | 12 failed / 0 passed ตามที่คาดก่อนแก้ |
| Focused ChatHistory after first fixes | 105 passed จากนั้น127 passed หลังเพิ่ม DDL/boundary cases |
| `dotnet build BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-restore` | 0 warnings / 0 errors |
| Development `dotnet test BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release --no-build` | **557 passed, 0 failed, 0 skipped** |
| Production คำสั่งเดียวกัน + ASPNETCORE_ENVIRONMENT=Production | **557 passed, 0 failed, 0 skipped** |
| `node --test 'tests/ui/*.test.mjs'` | **61 passed, 0 failed** |
| `python -I -X utf8 -m unittest discover -s tests -p 'test_*.py'` | **72 passed, OK, exit0** (181.476s) |
| Final `dotnet test ... --filter FullyQualifiedName~ChatHistory` หลังแก้คำอธิบาย tests | **129 passed, 0 failed, 0 skipped** |
| Preparation boundary tests ในชุดเต็ม | ไม่ register repository/context builder และ `/api/conversations` เป็น404 ทั้ง Development/Production |

SQL tests เป็น **static contract checks เท่านั้น**: collation/index/FK/state/size guard/rollback opt-in อยู่ในไฟล์
ไม่ได้รัน MySQL, ไม่ยืนยัน SQL syntax/execution/transactions/concurrency/durability จากผล static tests
Python มี ResourceWarning เดิมจากการเปิด CSV ใน `tests/test_benchmark_scripts.py` แต่ suite จบ OK
บันทึกไว้โดยไม่แก้ไฟล์ของทีม AI นอกขอบเขต BE-007 ไม่อ้างว่า output ทุกเครื่องมือไม่มี warning

## 4. Mutation evidence (isolated copy only)

สำเนาอยู่ `artifacts/be007-mutation/` ที่ Git-ignore ไม่แก้ Source จริง:

1. เปลี่ยน enum guard `||` เป็น `&&`: tests4กรณี fail
2. เปลี่ยน mixed-conversation guard ให้ยอมรับ2Conversation: test1กรณี fail
3. เปลี่ยน UserId collation ใน DDL กลับ ai_ci: static test1กรณี fail

รันพร้อมกันเฉพาะ6กรณี: **6 failed, exit1** ไม่ใช่ compile failure
คืนโค้ดทั้ง3จุดในสำเนาแล้ว rebuild/rerun: **6 passed, exit0**

## 5. Deliverable / acceptance disposition

| Deliverable / AC group | Status / evidence |
|---|---|
| D1 Schema, indexes, migration/rollback plan | Draft SQL + plan + static checks; real rehearsal Not Run |
| D2 Conversation APIs / pagination | Proposal + repository paging contract/mock tests; live APIs Not implemented |
| D3 Message persistence | Models/state rules/reference contract; durable DB storage Not implemented |
| D4 Multi-turn/context budget | Offline prototype tests; AI/template/tokenizer contract unresolved; not wired |
| D5 Streaming persistence / incomplete status | State tests only; SSE-to-database integration Not implemented |
| D6 Tests / IDOR / ownership | Reference owner isolation + regression + app gate tested; real auth/DB integration Not Run |
| D7 Docs / retention | Design, migration plan, API proposal, this evidence; retention policy pending data owner approval |
| Stable User ID | Opaque repository parameter only; real claim/source/length contract pending BE-005/006 |
| No cross-user leakage | Tested on test-only reference; no claim of deployed multi-user security |
| Existing Chat regression | Full .NET/JS tests passed; no Chat Controller/DI/Ollama/FE source changes in this issue |
| Code review | Author self-check only; independent reviewer approval pending |

## 6. Required before activation

- BE-005/006 complete, approved stable identity/authorization/CSRF/session contracts; never accept UserId from client
- Approve separate storage/provider/credentials/TLS and run real DB contract tests, concurrent sequence allocation and restart durability
- Approve API DTOs, retry/idempotency/write-conflict policy, streaming partial/crash recovery
- AI Engineer confirms multi-turn prompt/template and total token budget (system + current prompt + history + output + margin)
- Leader/data owner decides retention/deletion/backup erasure, reviews design and real migration/rollback rehearsal

ไม่ปิด Issue #23 และไม่ติ๊กว่าได้ครบตาม Acceptance Criteria จากผลเตรียมงานนี้
ไม่ได้รัน live Ollama/browser E2E ใหม่ เพราะไม่มีการเชื่อม prototype เข้ากับ request/streaming runtime
ไม่ได้แก้หรือใช้งานฐาน MySQL80/SEC-001 ไม่เพิ่ม package และไม่ migrate/delete ข้อมูลใด

เอกสาร: [Design](BE-007-DESIGN.md), [Migration plan](BE-007-MIGRATION-PLAN.md), [API proposal](BE-007-API-CONTRACT.md)
