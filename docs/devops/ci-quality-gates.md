# CI Quality Gates (DEVOPS-001)

เอกสารนี้อธิบายสิ่งที่ CI ตรวจ เงื่อนไขก่อน merge วิธีอ่านผล วิธีรันในเครื่อง และสิ่งที่ CI **ไม่** ครอบคลุม
ผลทดสอบเชิงลบของ pipeline อยู่ใน [ci-negative-test-results.md](ci-negative-test-results.md)
วิธีรันชุด live (ต้องมี Ollama/GPU) อยู่ใน [live-test-procedure.md](live-test-procedure.md)

## 1. สภาพแวดล้อมที่ตรวจแล้ว (3.1)

| ส่วน | เวอร์ชันที่โครงการใช้ | ที่มา |
| --- | --- | --- |
| .NET SDK | 10.0.x (project เป็น `net10.0`; เครื่องพัฒนาใช้ 10.0.402) | `BC-LocalAI.csproj` |
| Node.js | 20 ขึ้นไป (README); CI ใช้ 22; เครื่องพัฒนาใช้ 24.11 | README |
| Python | 3.11 (เครื่องพัฒนา 3.11.9) | README |
| PowerShell | **7 (`pwsh`)** ชุดทดสอบ Python ของงาน AI เรียก `pwsh`; ถ้าไม่มี test จะถูกข้าม | `tests/test_ai_helpers.py` |

เวลารันของแต่ละชุด (วัดบนเครื่องพัฒนา Windows 11 / RTX 4050 Laptop):

| ชุด | จำนวน test | เวลา |
| --- | ---: | ---: |
| `dotnet test BC-LocalAI.slnx` | 416 | ≈ 37 วินาที (รวม build) |
| `node --test "tests/ui/*.test.mjs"` | 23 | < 1 วินาที |
| `python -m unittest discover -s tests` | 72 ก่อนเพิ่มของ CI, 114 หลังเพิ่ม | ≈ 3 นาที 11 วินาที (ส่วนใหญ่ spawn `pwsh`) |

เวลารวมของ pipeline บน GitHub Actions ดูในตารางผลจริงของ [ci-negative-test-results.md](ci-negative-test-results.md)

## 2. Pipeline (3.2)

ไฟล์: `.github/workflows/ci.yml` ทำงานเมื่อ pull request เข้า `main`, push เข้า `main`, push ไป branch `ci-trial/**` (ใช้พิสูจน์ gate โดยไม่ต้องเปิด PR) และสั่งเองได้ (`workflow_dispatch`)

| Job | รันบน | ทำอะไร |
| --- | --- | --- |
| `hygiene` | ubuntu | สแกน secret, ตรวจลิงก์ในเอกสาร, รัน test ของสคริปต์ CI เอง |
| `tests` | windows (มี `pwsh` 7) | `dotnet test`, `node --test`, `python -m unittest discover`, ตรวจจำนวน test, เก็บ log เป็น artifact |

ไม่มี job ไหนต้องใช้ Ollama หรือ GPU และไม่ใช้ secret ของ repository (`permissions: contents: read`) ทุก action ล็อกด้วย commit SHA เต็ม ไม่ใช้ action ภายนอกที่ไม่ใช่ของ GitHub

## 3. Quality gates (3.3)

PR จะ merge ได้เมื่อ **ทุกข้อ** ผ่าน:

1. build และทุก test ผ่าน (.NET, JavaScript, Python)
2. ไม่มี test ถูกข้าม (`allowed_skipped = 0`) กัน test ที่ "ผ่าน" เพราะข้าม เช่นเมื่อไม่มี `pwsh` ซึ่งเคยข้ามไป 68 ข้อ
3. จำนวน test ที่รัน **ไม่น้อยกว่า** ค่าใน `docs/devops/test-baseline.json` ถ้าต้องลดจำนวน ต้องแก้ไฟล์นี้ใน PR เดียวกัน การลดจึงเห็นใน diff และต้องอธิบายใน PR
4. สแกน secret ไม่พบ (`scripts/ci/scan_secrets.py`)
5. ลิงก์ relative ในไฟล์ `.md` ทุกลิงก์ชี้ไปยังไฟล์ที่มีอยู่ (`scripts/ci/check_doc_links.py`)

### กติกาของตัวสแกน secret
- ตรวจ: private key block, AWS access key id, GitHub token, Slack token, connection string ที่มี password และการกำหนดค่าแบบ `ชื่อที่มี password/secret/token/api key = "ค่ายาว ≥ 8 ตัว"`
- ไม่พิมพ์ค่าที่พบ แสดงเฉพาะไฟล์ บรรทัด และชื่อกติกา
- ค่าตัวอย่างที่ชัดเจนว่าไม่ใช่ความลับ (`example`, `placeholder`, `<...>`, `your-...`) ไม่ถูกนับ
- กติกา "ชื่อ = ค่า" ไม่ใช้กับ `BC-LocalAI.Tests/`, `tests/` และ `BC-LocalAI/wwwroot/lib/` (ไลบรารีของบุคคลที่สาม) เพราะเป็นค่าปลอมหรือโค้ดภายนอก ส่วนกติกาเฉพาะรูปแบบ (private key, token, connection string) ใช้ทุกที่
- ค่าปลอมที่ต้องอยู่ในโค้ดจริงให้ใส่ `ci-allow-secret` ท้ายบรรทัด และใส่เฉพาะที่จำเป็น
- ตัวสแกนนี้เป็นตาข่ายขั้นต่ำ ไม่แทน secret scanning ของ GitHub หรือการรีวิว

### กติกาของตัวตรวจลิงก์
- ตรวจเฉพาะลิงก์ relative ที่ชี้ไฟล์ (ไม่ตรวจ anchor `#หัวข้อ`) ไม่เรียกลิงก์ภายนอก ผลจึงไม่ผันแปรตามเครือข่าย
- ลิงก์ในโค้ดบล็อกและ inline code ไม่ถูกนับ

## 4. การป้องกัน branch (3.4)

**ยังไม่ได้ตั้งค่า** ต้องให้ Leader (เจ้าของ repo) อนุมัติและตั้งเอง ข้อเสนอสำหรับ `main`:

1. Settings → Branches → Add branch ruleset (หรือ protection rule) สำหรับ `main`
2. Require a pull request before merging, อย่างน้อย 1 approval
3. Require status checks to pass: `Secret scan and doc links` และ `Build and tests (Windows, PowerShell 7)`
4. Require branches to be up to date before merging
5. ห้าม push ตรง, ห้าม force push, ห้ามลบ branch `main`

ชื่อ check ต้องตรงกับ `name:` ของ job ใน workflow และ check จะปรากฏให้เลือกได้หลังจาก workflow รันสำเร็จอย่างน้อยหนึ่งครั้ง

## 5. วิธีอ่านผล
- หน้า PR → ส่วน Checks → เปิด job ที่ล้ม แต่ละ step มีชื่อบอกชนิดความล้มเหลว
- สรุปจำนวน test อยู่ที่หน้า Summary ของ run (ตาราง "Test count gate")
- log ดิบของ test อยู่ใน artifact `test-logs` (เก็บ 7 วัน)

## 6. วิธีรันในเครื่อง

```bash
dotnet test BC-LocalAI.slnx
node --test "tests/ui/*.test.mjs"
python -m unittest discover -s tests          # ต้องมี pwsh 7 ใน PATH
python scripts/ci/scan_secrets.py
python scripts/ci/check_doc_links.py
```

ตรวจจำนวน test แบบเดียวกับ CI (เก็บ log ก่อน):

```bash
dotnet test BC-LocalAI.slnx 2>&1 | tee dotnet.log
node --test "tests/ui/*.test.mjs" 2>&1 | tee node.log
python -m unittest discover -s tests 2>&1 | tee python.log
python scripts/ci/check_test_counts.py --baseline docs/devops/test-baseline.json --dotnet dotnet.log --node node.log --python python.log
```

หยุดแอปที่รันอยู่ก่อน `dotnet test` (ไฟล์ output ถูกล็อก ดู README)

## 7. สิ่งที่ CI ไม่ครอบคลุม
- **ชุด live** (AI-003, AI-004, AI-005 กับ Ollama จริง): ต้องมี Ollama และ GPU จึงรันบน runner ทั่วไปไม่ได้ ใช้ขั้นตอนใน [live-test-procedure.md](live-test-procedure.md)
- ความปลอดภัยของ dependency (ต้องตัดสินแยก), การ deploy (DEVOPS-002), ประสิทธิภาพ (AI-006)
- ความถูกต้องของคำตอบ AI: test ใน CI ใช้ mock ไม่ได้พิสูจน์คุณภาพโมเดล
- ลิงก์ภายนอกและ anchor ในเอกสาร
- ถ้า GitHub Actions ปิดหรือไม่ทำงาน gate ทั้งหมดจะไม่ทำงาน ผู้รีวิวต้องไม่ merge โดยไม่มีผลของ check
