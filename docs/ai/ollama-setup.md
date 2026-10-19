# AI-001: Installation guide (Windows 11)

ขอบเขต: ติดตั้งและทดสอบ Ollama + `qwen3:4b` ในเครื่องเท่านั้น ไม่รวม Backend API, Web UI, fine-tuning หรือเปิด API สู่ Internet

## 1. ตรวจเครื่อง

Issue กำหนด Windows 11, RAM 16 GB และ NVIDIA RTX 4060/4070 ตรวจเครื่องก่อนติดตั้ง:

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version
Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory
nvidia-smi --query-gpu=name,memory.total --format=csv,noheader
Get-PSDrive C
```

ใช้ `nvidia-smi` ตรวจ VRAM จริง เพราะค่า AdapterRAM จาก WMI อาจไม่ตรงสำหรับ GPU รุ่นใหม่ เตรียมพื้นที่สำหรับโมเดลประมาณ 2.5 GB พร้อมพื้นที่เผื่อ runtime/cache และต้องเชื่อมต่อ Internet ตอนดาวน์โหลด

## 2. ติดตั้งและเปิด Ollama

ดาวน์โหลดจาก [Ollama สำหรับ Windows](https://ollama.com/download/windows) และติดตั้งด้วยสิทธิ์ที่ได้รับอนุญาต จากนั้นเปิด Ollama และเปิด PowerShell ใหม่:

```powershell
ollama --version
Invoke-RestMethod http://127.0.0.1:11434/api/version
```

บน Windows ตัวติดตั้งปกติรันเป็นแอปเบื้องหลัง ไม่จำเป็นต้องมี Windows Service ถ้า API ไม่ตอบ ให้เปิดแอป Ollama ก่อน หรือใช้ `ollama serve` ใน terminal แยกเมื่อยังไม่มี listener อย่าเปิดซ้ำเมื่อ port ถูกใช้งานแล้ว

ใช้ loopback ค่าเริ่มต้นเท่านั้น ไม่ตั้ง `OLLAMA_HOST=0.0.0.0` ไม่เพิ่ม firewall rule หรือ port forwarding

## 3. ดาวน์โหลดและทดสอบ CLI

```powershell
ollama pull qwen3:4b
ollama list
ollama run qwen3:4b 'Return a JSON object with a single key greeting and value Hello.' --think=false --format json
ollama ps
```

ต้องเห็น `qwen3:4b` และ CLI ตอบข้อความได้ การโหลดครั้งแรกอาจช้า ตรวจ `ollama ps` เพื่อดูการใช้ GPU/CPU จริง

## 4. ทดสอบ API จากราก repo

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -OutputPath .\docs\ai\ollama-test-result.json
```

คำสั่งที่ตรวจสอบแล้วใช้ PowerShell 7 (`pwsh`) ตรวจ `pwsh --version` ก่อน หากยังไม่มีให้ติดตั้งตาม [คู่มือ Microsoft](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) สคริปต์มี syntax ขั้นต่ำสำหรับ PowerShell 5.1 และบันทึก UTF-8 BOM แต่ยังไม่ได้ยืนยันการรันบน 5.1 เพราะ execution policy ของเครื่องทดสอบบล็อกไว้

ผลสำเร็จต้องเป็น `PASS`, HTTP 200, `done=true`, `done_reason=stop` และ JSON answer ไม่ว่าง รหัสออกคือ 0; ถ้าผิดพลาดรหัสออกคือ 1 ตรวจ `$LASTEXITCODE` ทันทีหลังคำสั่ง หากนโยบายเครื่องบล็อก script ให้ติดต่อผู้ดูแลตามนโยบายองค์กร ดูวิธีใช้ด้วย `Get-Help .\scripts\test-ollama.ps1 -Full`

## 5. ทดสอบเมื่อ Ollama ไม่พร้อม

ตรวจว่า port ทดสอบว่างก่อน:

```powershell
Get-NetTCPConnection -LocalPort 11435 -State Listen -ErrorAction SilentlyContinue
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435
$LASTEXITCODE
```

หากมี listener อยู่ ให้เลือก port ว่างอื่น คาดหวัง `FAIL while checking Ollama availability` พร้อมคำแนะนำและ exit 1 วิธีนี้จำลอง endpoint ไม่พร้อมโดยไม่หยุด Ollama ที่คนอื่นอาจใช้อยู่ ไม่ใช่การทดสอบ shutdown ของ service จริง

## Troubleshooting

- เชื่อมต่อไม่ได้: เปิด Ollama และตรวจ `/api/version`; ตรวจ port ให้ถูกต้อง
- ไม่พบโมเดล/HTTP 404: `ollama pull qwen3:4b` แล้วลองใหม่
- Timeout: เพิ่ม `-TimeoutSec 300` และตรวจหน่วยความจำ/GPU
- หน่วยความจำไม่พอ: ปิดงานที่ใช้ GPU มาก แล้วลองใหม่ อย่าเปลี่ยนโมเดลเป็นรุ่นอื่นเพื่ออ้างว่าผ่าน issue

ดู [API guide](ollama-api.md) และ [ผลทดสอบ](ollama-test-evidence.md) สำหรับรายละเอียดการตรวจรับ ผลจากเครื่อง Windows 11 นี้ไม่ใช่หลักฐานการติดตั้ง Windows Server
