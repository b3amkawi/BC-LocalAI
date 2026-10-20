# CI negative test results (DEVOPS-001 D4)

พิสูจน์ว่า pipeline จับความล้มเหลวได้จริงบน GitHub Actions ใช้ branch ทดลอง `ci-trial/**` ที่ push เพื่อให้ workflow ทำงาน **โดยไม่เปิด PR** (ตามที่ตกลงว่าไม่ใช้ PR) แต่ละ branch ใส่ข้อบกพร่อง **เพียงข้อเดียว** จึงต้องแยกหลาย branch เพื่อรู้ว่า gate ตัวไหนจับอะไร ค่าลับที่ใช้เป็นค่าปลอม และ branch ทดลองถูกลบหลังเก็บผล (ผลของ run ยังอยู่ในแท็บ Actions)
Issue ต้องการ 3 แบบ (test ล้ม, secret ปลอม, ลิงก์เสีย) ทดสอบ 8 แบบ

## ผลการทดสอบ

| Branch ทดลอง | ข้อบกพร่องที่ใส่ | ที่คาดว่าจะจับได้ | ผล |
| --- | --- | --- | --- |
| `neg-dotnet` | test .NET ที่ล้มโดยตั้งใจ | `dotnet build and test` | **จับได้** |
| `neg-node` | test JavaScript ที่ล้ม | `JavaScript tests` | **จับได้** |
| `neg-python` | test Python ที่ล้ม | `Python tests` | **จับได้** |
| `neg-secret-assignment` | ค่าปลอมในรูป `api_key = "..."` | `Secret scan` | **จับได้** |
| `neg-secret-connection` | connection string ปลอมที่มีรหัสผ่าน | `Secret scan` | **จับได้** |
| `neg-link` | ลิงก์ Markdown ไปไฟล์ที่ไม่มี | `Markdown links` | **จับได้** |
| `neg-count-drop` | ลบไฟล์ test ทั้งไฟล์ (test ที่เหลือผ่านหมด) | `Test count gate` | **จับได้** (ตัวเดียวที่ล้ม) |
| `neg-skip` | test ที่ถูก `skip` | `Test count gate` | **จับได้** (ตัวเดียวที่ล้ม) |

## ผลดิบของทุก run (สร้างด้วย `scripts/ci/summarize-trial-runs.py`)

| Branch | ผลของ run | job/step ที่หยุด | เวลา Windows job | เวลา hygiene job |
| --- | --- | --- | ---: | ---: |
| `clean` (run 39417232539) | failure | Build and tests (Windows, PowerShell 7): Python tests, Test count gate; Secret scan and doc links: Tests of the CI scripts themselves | 5 นาที 14 วินาที | 0 นาที 15 วินาที |
| `clean-2` (run 39418062633) | failure | Build and tests (Windows, PowerShell 7): Test count gate | 5 นาที 7 วินาที | 0 นาที 14 วินาที |
| `clean-3` (run 39418258428) | success | - | 4 นาที 31 วินาที | 0 นาที 14 วินาที |
| `neg-dotnet` (run 39419145440) | failure | Build and tests (Windows, PowerShell 7): dotnet build and test, Test count gate | 1 นาที 48 วินาที | 0 นาที 17 วินาที |
| `neg-node` (run 39419150407) | failure | Build and tests (Windows, PowerShell 7): JavaScript tests, Test count gate | 1 นาที 47 วินาที | 0 นาที 18 วินาที |
| `neg-python` (run 39419155565) | failure | Build and tests (Windows, PowerShell 7): Python tests, Test count gate | 5 นาที 8 วินาที | 0 นาที 19 วินาที |
| `neg-secret-assignment` (run 39419160682) | failure | Secret scan and doc links: Secret scan | 4 นาที 9 วินาที | 0 นาที 6 วินาที |
| `neg-secret-connection` (run 39419165259) | failure | Secret scan and doc links: Secret scan | 5 นาที 3 วินาที | 0 นาที 6 วินาที |
| `neg-link` (run 39419170098) | failure | Secret scan and doc links: Markdown links | 5 นาที 3 วินาที | 0 นาที 7 วินาที |
| `neg-count-drop` (run 39419174904) | failure | Build and tests (Windows, PowerShell 7): Test count gate | 4 นาที 30 วินาที | 0 นาที 14 วินาที |
| `neg-skip` (run 39419180204) | failure | Build and tests (Windows, PowerShell 7): Test count gate | 5 นาที 5 วินาที | 0 นาที 16 วินาที |
| `clean-4` (run 39419184849) | success | - | 5 นาที 35 วินาที | 0 นาที 19 วินาที |

## สิ่งที่ CI จับได้ใน run แรก ๆ ของตัวมันเอง

1. `clean`: `scripts/ci/new-live-evidence.ps1` เรียก `Get-CimInstance` ที่ไม่มีบน Linux และ `nvidia-smi` ที่ไม่มีบน runner แก้ให้ใช้เมื่อมีคำสั่งนั้นเท่านั้น
2. `clean-2`: test `test_bundled_runtime_preserves_child_exit_code` (เดิมใน `main`) ถูกข้ามบนทุกเครื่องที่ไม่มี PowerShell ที่มากับ Codex gate เดิมที่ห้ามข้ามเลยจึงล้ม แก้ให้ baseline ระบุชื่อ test ที่ข้ามได้เพียงตัวเดียว การข้ามอื่นยังล้ม (พิสูจน์ด้วย `neg-skip`)
3. `clean-3` และ `clean-4`: ผ่านทั้งสองงาน

## เวลารวมของ pipeline

run สะอาดสองครั้ง: Windows job 4 นาที 31 วินาที และ 5 นาที 35 วินาที, hygiene job 14 และ 19 วินาที ทำขนานกัน เวลารวม ≈ 4.5 ถึง 5.6 นาที ส่วนใหญ่เป็น Python test (≈ 3 นาที) ถ้า test .NET หรือ JavaScript ล้ม จบใน ≈ 1 นาที 48 วินาที ถ้า secret หรือลิงก์ล้ม งาน hygiene จบใน 6 ถึง 7 วินาที

## รันซ้ำด้วย branch เดียว (`ci-trial/negative`)

หลังรวม branch ทดลองเป็นอันเดียว (`scripts/ci/negative-trials.sh`) รันซ้ำทั้ง 8 ข้อบกพร่องบน base ใหม่ ผลตรงกับตารางด้านบนทุกข้อและแต่ละข้อล้มที่ step ที่คาดไว้เท่านั้น:

| ข้อบกพร่อง | job / step ที่ล้ม |
| --- | --- |
| test .NET ล้ม | Build and tests: dotnet build and test, Test count gate |
| test JavaScript ล้ม | Build and tests: JavaScript tests, Test count gate |
| test Python ล้ม | Build and tests: Python tests, Test count gate |
| secret ปลอมในรูป `api_key = "..."` | Secret scan and doc links: Secret scan |
| connection string ปลอมที่มีรหัสผ่าน | Secret scan and doc links: Secret scan |
| ลิงก์ Markdown ไปไฟล์ที่ไม่มี | Secret scan and doc links: Markdown links |
| ลบไฟล์ test ทั้งไฟล์ | Build and tests: Test count gate (อย่างเดียว) |
| test ที่ถูก skip | Build and tests: Test count gate (อย่างเดียว) |

ครั้งแรกที่ใช้ branch เดียวล้มทุกข้อที่ step Secret scan เพราะตัวสคริปต์ `negative-trials.sh` มีค่าปลอมที่ตัวสแกนจับได้เอง ซึ่งบังผลที่ตั้งใจทดสอบ (ลิงก์เสียก็ไม่ถูกตรวจเพราะ step ก่อนหน้าล้ม) จึงใส่ `ci-allow-secret` ที่บรรทัดนั้นและรันใหม่ ผลข้างบนคือรันใหม่นี้

## ข้อจำกัด
- ทดสอบด้วย push ไป branch ไม่ใช่ PR จริง workflow เดียวกันทำงานบน `pull_request` แต่ยังไม่เห็นผลบน PR จริง ซึ่ง Definition of Done ของ Issue ต้องการ
- branch protection ยังไม่ได้ตั้ง (ต้องให้ Leader อนุมัติ ดู [ci-quality-gates.md](ci-quality-gates.md))
- GitHub แจ้งว่า action v4 ที่ใช้กำลังเลิกใช้ Node.js 20 (ถูกบังคับเป็น Node.js 24) ยังทำงานได้ ควรอัปเดตภายหลัง
- ตัวสแกน secret เป็นตาข่ายขั้นต่ำ ไม่แทน secret scanning ของ GitHub
