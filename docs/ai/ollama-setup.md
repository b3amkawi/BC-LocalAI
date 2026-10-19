# AI-001: คู่มือติดตั้ง Ollama และ Qwen3:4b บน Windows 11

คู่มือนี้เขียนสำหรับผู้ที่ยังไม่คุ้นเคยกับ Terminal หรือ PowerShell ทำตามทีละขั้นได้โดยไม่ต้องมีพื้นฐานเขียนโปรแกรม ขอบเขตคือการติดตั้งและทดสอบ Ollama กับ `qwen3:4b` ภายในเครื่องเท่านั้น ไม่รวม Backend API, Web UI, fine-tuning หรือการเปิด API สู่อินเทอร์เน็ต

## 1. เตรียมเครื่อง

ต้องมีสิทธิ์ติดตั้งโปรแกรม อินเทอร์เน็ต และพื้นที่ว่างอย่างน้อยประมาณ 5 GB เพื่อเผื่อตัวโปรแกรม โมเดล และ cache ข้อกำหนดของ Issue คือ Windows 11, RAM 16 GB และ NVIDIA RTX 4060/4070

### เปิด PowerShell ให้ถูกโปรแกรม

1. กดปุ่ม **Windows** หรือคลิก Start
2. พิมพ์ `PowerShell`
3. คลิก **Windows PowerShell** หรือ **PowerShell**
4. ตรวจว่าบรรทัดรับคำสั่งขึ้นต้นด้วย `PS` เช่น `PS C:\Users\ชื่อผู้ใช้>`

หากเห็น `C:\Users\ชื่อผู้ใช้>` โดยไม่มี `PS` แสดงว่าเปิด **Command Prompt (cmd)** อยู่ คำสั่ง PowerShell เช่น `irm` จะใช้ตรงนั้นไม่ได้

คัดลอกคำสั่งต่อไปนี้ทีละบรรทัด วางด้วย `Ctrl+V` แล้วกด Enter:

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version
Get-CimInstance Win32_ComputerSystem | Select-Object @{Name='RAM_GB';Expression={[math]::Round($_.TotalPhysicalMemory / 1GB, 1)}}
nvidia-smi --query-gpu=name,memory.total --format=csv,noheader
Get-PSDrive C
```

ผลลัพธ์ควรแสดง Windows 11, RAM ใกล้เคียง 16 GB, ชื่อ GPU/VRAM และพื้นที่ว่างไดรฟ์ C หาก `nvidia-smi` ไม่รู้จัก ให้ติดตั้งหรืออัปเดต NVIDIA Driver หรือสอบถามผู้ดูแลเครื่อง อย่าใช้ค่า `AdapterRAM` จาก WMI แทน เพราะอาจแสดง VRAM ของ GPU รุ่นใหม่ผิด

## 2. ติดตั้ง Ollama

เลือกทำเพียงวิธีเดียว สำหรับผู้ใช้ทั่วไปแนะนำ **วิธี A** เพราะมองเห็นขั้นตอนและไม่ต้องรันสคริปต์จากอินเทอร์เน็ต

### วิธี A: ใช้ตัวติดตั้ง (แนะนำสำหรับมือใหม่)

1. เปิด [หน้าดาวน์โหลด Ollama สำหรับ Windows](https://ollama.com/download/windows)
2. กด **Download for Windows**
3. เปิดไฟล์ที่ดาวน์โหลด เช่น `OllamaSetup.exe`
4. หาก Windows ถามยืนยัน ให้ตรวจว่าไฟล์มาจาก `ollama.com` แล้วอนุญาตตามนโยบายเครื่อง
5. รอจนติดตั้งเสร็จ Ollama จะเปิดเป็นแอปเบื้องหลัง
6. ปิด PowerShell หน้าต่างเดิมแล้วเปิดใหม่ เพื่อให้ Windows โหลดคำสั่ง `ollama` เข้า PATH

### วิธี B: ใช้คำสั่ง PowerShell

เว็บไซต์ Ollama ระบุคำสั่งนี้สำหรับ Windows:

```powershell
irm https://ollama.com/install.ps1 | iex
```

ต้องวางใน **PowerShell** เท่านั้น `irm` ย่อจาก `Invoke-RestMethod` ซึ่งดาวน์โหลดสคริปต์ และ `iex` ย่อจาก `Invoke-Expression` ซึ่งรันสคริปต์นั้น ใช้เฉพาะ URL ทางการ `https://ollama.com/install.ps1`

### แก้ปัญหา `irm is not recognized`

สาเหตุที่พบบ่อยที่สุดคือพิมพ์ใน Command Prompt:

- ถูกหน้าต่าง: `PS C:\Users\ชื่อผู้ใช้>`
- ผิดหน้าต่าง: `C:\Users\ชื่อผู้ใช้>`

ถ้าอยู่ใน Command Prompt ให้พิมพ์:

```text
powershell
```

เมื่อ prompt เปลี่ยนเป็น `PS ...>` ให้ใช้ชื่อคำสั่งเต็มแทนชื่อย่อ:

```powershell
Invoke-RestMethod https://ollama.com/install.ps1 | Invoke-Expression
```

หรือกลับไปใช้ตัวติดตั้งใน **วิธี A** ได้ทันที ถ้าเปิด PowerShell ถูกต้องแต่ยังไม่รู้จักทั้งสองชื่อ ให้ตรวจ:

```powershell
$PSVersionTable.PSVersion
Get-Command Invoke-RestMethod
```

Windows 11 ปกติมี Windows PowerShell 5.1 ซึ่งรองรับคำสั่งนี้ หากไม่มี ให้ใช้วิธี A หรือติดต่อผู้ดูแลเครื่อง ไม่ควรลดนโยบายความปลอดภัยทั้งเครื่อง

ข้อผิดพลาดอื่น:

- **Running scripts is disabled**: ใช้วิธี A หรือติดต่อผู้ดูแล ไม่ต้องตั้ง `ExecutionPolicy` เป็น `Unrestricted`
- **Access is denied / ต้องใช้ Administrator**: คลิกขวา PowerShell แล้วเลือก **Run as administrator** เฉพาะเมื่อได้รับสิทธิ์
- **TLS, proxy หรือ certificate error**: ลองเปิด URL ดาวน์โหลดในเบราว์เซอร์ ตรวจอินเทอร์เน็ต วันเวลาของ Windows, VPN/proxy หรือติดต่อผู้ดูแลเครือข่าย
- **โปรแกรมป้องกันไวรัสบล็อก**: อย่าปิดระบบป้องกันเอง ให้ผู้ดูแลตรวจไฟล์และ URL ทางการ

## 3. ตรวจว่า Ollama ทำงาน

เปิด PowerShell ใหม่แล้วรัน:

```powershell
ollama --version
Invoke-RestMethod http://127.0.0.1:11434/api/version
```

คำสั่งแรกต้องแสดงเวอร์ชัน คำสั่งที่สองต้องคืนข้อมูลเวอร์ชันจาก API หากไม่รู้จัก `ollama` ให้ปิดและเปิด PowerShell ใหม่ เปิด Ollama จาก Start Menu แล้วลอง `Get-Command ollama` หากยังไม่พบ ให้ sign out/sign in หรือ restart Windows หนึ่งครั้ง

ตัวติดตั้ง Windows เปิด Ollama เป็นแอปเบื้องหลัง จึงไม่จำเป็นต้องเห็น Windows Service หาก API ไม่ตอบ ให้เปิดแอป **Ollama** จาก Start Menu หรือเปิด PowerShell อีกหน้าต่างแล้วรัน `ollama serve` ค้างไว้ อย่ารันซ้ำเมื่อ port `11434` ถูกใช้งานแล้ว

ใช้ `127.0.0.1` ตามค่าเริ่มต้น ห้ามตั้ง `OLLAMA_HOST=0.0.0.0`, เพิ่ม firewall rule หรือทำ port forwarding เพราะ Issue นี้ไม่ให้เปิด API สู่อินเทอร์เน็ต

## 4. ดาวน์โหลด Qwen3:4b

```powershell
ollama pull qwen3:4b
```

ไฟล์มีขนาดหลาย GB รอจน progress ครบและขึ้นว่าสำเร็จ จากนั้นตรวจ:

```powershell
ollama list
```

ต้องมี `qwen3:4b` หากดาวน์โหลดค้าง ให้ตรวจอินเทอร์เน็ตและพื้นที่ว่างแล้วรันคำสั่งเดิมซ้ำ ระบบจะดาวน์โหลดต่อหรือทวนส่วนที่ต้องใช้

## 5. ทดลองถามผ่าน Command Line

```powershell
ollama run qwen3:4b
```

เมื่อเห็น `>>>` ให้พิมพ์คำถาม เช่น `สวัสดี ช่วยตอบสั้น ๆ` แล้วกด Enter ครั้งแรกอาจช้าเพราะกำลังโหลดโมเดล พิมพ์ `/bye` เพื่อออก

ทดสอบแบบ JSON และดูการใช้ GPU/CPU:

```powershell
ollama run qwen3:4b "Return a JSON object with a single key greeting and value Hello." --think=false --format json
ollama ps
```

## 6. เปิด PowerShell ที่โฟลเดอร์ Repository

เปิดโฟลเดอร์ repository ใน File Explorer คลิกช่อง address bar พิมพ์ `powershell` แล้วกด Enter จากนั้นตรวจตำแหน่ง:

```powershell
Get-Location
Test-Path .\scripts\test-ollama.ps1
```

บรรทัดที่สองต้องเป็น `True` หากเป็น `False` แสดงว่าอยู่ผิดโฟลเดอร์ ให้ใช้ `cd` ตามด้วย path เช่น:

```powershell
cd "C:\Users\ชื่อผู้ใช้\Documents\BC-LocalAI"
```

## 7. ทดสอบ REST API ด้วยสคริปต์โปรเจกต์

ตรวจ PowerShell 7 ก่อน:

```powershell
pwsh --version
```

ถ้าแสดงเวอร์ชัน ให้รันจากราก repository:

```powershell
pwsh -NoProfile -File .\scripts\test-ollama.ps1
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -OutputPath .\docs\ai\ollama-test-result.json
```

หาก `pwsh is not recognized` ให้ติดตั้งตาม [คู่มือ PowerShell บน Windows ของ Microsoft](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) แล้วเปิด terminal ใหม่ `powershell` และ `pwsh` เป็นคนละโปรแกรม: Windows PowerShell เดิมใช้ `powershell`; PowerShell 7 ใช้ `pwsh`

ผลสำเร็จต้องมี `PASS`, HTTP 200, `done=true`, `done_reason=stop` และคำตอบไม่ว่าง ตรวจรหัสออกทันที:

```powershell
$LASTEXITCODE
```

ต้องได้ `0`; เมื่อผิดพลาดจะได้ `1` พร้อมข้อความระบุขั้นตอน ดูตัวเลือกทั้งหมดด้วย `Get-Help .\scripts\test-ollama.ps1 -Full`

## 8. เปิดหน้าแชทของโปรเจกต์

ดับเบิลคลิก `scripts\open-chat.cmd` หรือรันจากราก repository:

```powershell
.\scripts\open-chat.cmd
```

รอจนเห็น `Qwen3 Local AI พร้อมใช้งาน` และ prompt `คุณ:` แล้วพิมพ์เฉพาะคำถาม อย่าพิมพ์คำถามที่ prompt `PS C:\...>` เพราะ PowerShell จะคิดว่าเป็นชื่อคำสั่ง พิมพ์ `/bye` หลัง `คุณ:` เพื่อออก

## 9. ทดสอบกรณี Ollama ไม่พร้อม

ใช้ port ที่ไม่มี service เพื่อไม่ต้องปิด Ollama ของผู้อื่น:

```powershell
Get-NetTCPConnection -LocalPort 11435 -State Listen -ErrorAction SilentlyContinue
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435
$LASTEXITCODE
```

ถ้าคำสั่งแรกไม่แสดงอะไร แปลว่า port ว่าง ผลที่คาดหวังคือ `FAIL while checking Ollama availability` พร้อมคำแนะนำ และรหัสออก `1`

## 10. ปัญหาที่พบบ่อย

| อาการ | สาเหตุที่เป็นไปได้ | วิธีแก้ |
| --- | --- | --- |
| `irm is not recognized` | ใช้ Command Prompt | เปิด PowerShell ให้ prompt ขึ้นต้น `PS` หรือใช้ตัวติดตั้ง `.exe` |
| `ollama is not recognized` | terminal ยังไม่โหลด PATH หรือติดตั้งไม่สำเร็จ | เปิด terminal ใหม่ เปิด Ollama แล้วลอง `Get-Command ollama` |
| `pwsh is not recognized` | ไม่มี PowerShell 7 | ติดตั้ง PowerShell 7 ตามคู่มือ Microsoft |
| ต่อ `127.0.0.1:11434` ไม่ได้ | Ollama ยังไม่เปิด | เปิด Ollama หรือรัน `ollama serve` ในอีกหน้าต่าง |
| HTTP 404 / ไม่พบโมเดล | ยังไม่มี `qwen3:4b` | รัน `ollama pull qwen3:4b` แล้วตรวจ `ollama list` |
| ตอบช้าหรือ timeout | โหลดครั้งแรกหรือ RAM/VRAM ไม่พอ | ปิดงานที่ใช้ GPU แล้วทดสอบด้วย `-TimeoutSec 300` |
| คำถามกลายเป็น error สีแดง | พิมพ์ที่ prompt `PS ...>` | เปิด `scripts\open-chat.cmd` แล้วรอ prompt `คุณ:` |
| port 11434 ถูกใช้งานแล้ว | Ollama ทำงานอยู่แล้ว | ไม่ต้องเปิด `ollama serve` ซ้ำ ให้ทดสอบ API ต่อ |

อย่าเปลี่ยนโมเดลเพื่ออ้างว่า Issue ผ่าน เพราะเกณฑ์ระบุ `qwen3:4b` ดู [คู่มือ API](ollama-api.md) และ [หลักฐานการทดสอบ](ollama-test-evidence.md) เพิ่มเติม ผลจาก Windows 11 เป็นหลักฐานของเครื่องทดสอบนี้ ไม่ใช่หลักฐาน Windows Server
