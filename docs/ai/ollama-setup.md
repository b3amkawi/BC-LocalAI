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

สคริปต์ทดสอบไม่ได้มากับ Ollama แต่เก็บอยู่ใน Repository `BC-LocalAI` เครื่องใหม่จึงต้องดาวน์โหลด Repository ก่อน เลือกวิธีใดวิธีหนึ่งต่อไปนี้

### กรณี clone ไม่ได้หรือไม่มีสิทธิ์ดาวน์โหลด

หาก Git แสดง `Repository not found`, GitHub เปิด Repository ไม่ได้ หรือบัญชีไม่มีสิทธิ์ดาวน์โหลดไฟล์ ให้ทำดังนี้:

1. ติดต่อฝ่าย Software หรือเจ้าของ Repository เพื่อขอสิทธิ์เข้าถึง `b3amkawi/BC-LocalAI` และ branch `pao01_AI`
2. แจ้งข้อความ error ที่เห็น ชื่อบัญชี GitHub และเวลาที่พบปัญหา โดยห้ามส่งรหัสผ่านหรือ access token
3. ระหว่างรอสิทธิ์ ให้ข้ามการทดสอบสคริปต์ของ Repository แล้วทดสอบ Ollama โดยตรงตามขั้นตอนด้านล่าง
4. เมื่อได้รับสิทธิ์แล้ว ให้กลับมาทำข้อ 6 และข้อ 7 เพื่อยืนยันสคริปต์ของโปรเจกต์

ตรวจ Ollama และโมเดล:

```powershell
ollama --version
Invoke-RestMethod http://127.0.0.1:11434/api/version
ollama list
ollama run qwen3:4b
```

เมื่อ `ollama run` แสดง `>>>` ให้พิมพ์คำถาม แล้วพิมพ์ `/bye` เพื่อออก จากนั้นทดสอบ REST API โดยตรง:

```powershell
$body = @{
    model  = 'qwen3:4b'
    prompt = 'สวัสดี กรุณาตอบสั้น ๆ'
    stream = $false
    think  = $false
} | ConvertTo-Json

$response = Invoke-RestMethod `
    -Uri 'http://127.0.0.1:11434/api/generate' `
    -Method Post `
    -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body))

$response | Select-Object model, response, done, done_reason
```

ผลที่ผ่านต้องมี `model` เป็น `qwen3:4b`, `response` มีคำตอบ, `done` เป็น `True` และ `done_reason` เป็น `stop` บันทึกหลักฐานชั่วคราวบน Desktop ได้ด้วย:

```powershell
$response | ConvertTo-Json -Depth 10 | Set-Content "$HOME\Desktop\ollama-test-result.json" -Encoding UTF8
```

ทดสอบ endpoint ไม่พร้อมโดยใช้ port ที่ไม่มี service:

```powershell
try {
    Invoke-RestMethod -Uri 'http://127.0.0.1:11435/api/version' -TimeoutSec 5
    Write-Host 'ไม่ผ่าน: port นี้มี service ทำงานอยู่'
}
catch {
    Write-Host 'ผ่าน: แสดงข้อผิดพลาดเมื่อ Ollama endpoint ไม่พร้อม'
    Write-Host $_.Exception.Message
}
```

ระหว่างรอสิทธิ์ สามารถยืนยันการตรวจเครื่อง, การติดตั้ง Ollama, โมเดล `qwen3:4b`, CLI, REST API และกรณี endpoint ไม่พร้อมได้ แต่ยังห้ามติ๊กว่าผ่านหัวข้อสคริปต์ตาม README, ไฟล์ส่งมอบใน Repository, การตรวจ secret หรือการเปิดแชทด้วย `open-chat.cmd` จนกว่าจะได้ไฟล์จากฝ่าย Software และทดสอบข้อ 6–8 ครบ

### วิธี A: ดาวน์โหลด ZIP (แนะนำสำหรับผู้ที่ยังไม่ใช้ Git)

1. เปิดหน้า [BC-LocalAI บน GitHub](https://github.com/b3amkawi/BC-LocalAI) และลงชื่อเข้าใช้บัญชีที่ได้รับสิทธิ์ ถ้า Repository เปิดไม่ได้ให้ขอสิทธิ์จากเจ้าของก่อน
2. กดตัวเลือก branch บริเวณด้านซ้ายเหนือรายการไฟล์ แล้วเลือก `pao01_AI`
3. ตรวจว่าชื่อ branch บนหน้าจอเป็น `pao01_AI`
4. กดปุ่มสีเขียว **Code** แล้วเลือก **Download ZIP**
5. เปิดโฟลเดอร์ Downloads คลิกขวาไฟล์ ZIP แล้วเลือก **Extract All...** และกด **Extract**
6. เข้าโฟลเดอร์ที่แตกแล้ว ชื่อโดยทั่วไปคือ `BC-LocalAI-pao01_AI` ภายในต้องเห็น `README.md`, `docs` และ `scripts`

ห้ามเปิด PowerShell ขณะยังดูไฟล์อยู่ภายใน ZIP เพราะสคริปต์อาจเข้าถึงไฟล์ข้างเคียงไม่ได้ ต้อง **Extract All** ก่อนเสมอ

วิธี ZIP ไม่มีคำสั่ง `git pull` หากต้องการไฟล์รุ่นใหม่ในภายหลัง ให้ดาวน์โหลด ZIP จาก branch `pao01_AI` ใหม่และแตกเป็นโฟลเดอร์ใหม่ เพื่อไม่ให้ปะปนกับไฟล์เดิม

### วิธี B: Clone ด้วย Git

#### B1. ติดตั้ง Git for Windows

ตรวจว่ามี Git อยู่แล้วหรือไม่:

```powershell
git --version
```

ถ้าแสดงเลขเวอร์ชัน ให้ข้ามไปหัวข้อ B2 หากขึ้นว่า `git is not recognized` ให้ติดตั้งด้วยวิธีใดวิธีหนึ่ง:

**ติดตั้งด้วยไฟล์ Setup (แนะนำสำหรับมือใหม่)**

1. เปิด [Git for Windows จากเว็บไซต์ทางการ](https://git-scm.com/download/win)
2. ดาวน์โหลด **64-bit Git for Windows Setup** เครื่อง Windows on ARM ให้เลือก ARM64
3. เปิดไฟล์ Setup ที่ดาวน์โหลด
4. ใช้ค่าเริ่มต้นของตัวติดตั้งได้ เมื่อถึงหน้าเลือก PATH ให้เลือกตัวเลือกที่อนุญาตให้ใช้ Git จาก command line และโปรแกรมภายนอก
5. กด Install แล้วรอจนเสร็จ
6. ปิด PowerShell เดิมและเปิดใหม่

หรือติดตั้งผ่าน `winget` ใน PowerShell:

```powershell
winget install --id Git.Git -e --source winget
```

ถ้า `winget` ไม่รู้จัก ให้ใช้ไฟล์ Setup ด้านบน หลังติดตั้งเสร็จตรวจ:

```powershell
git --version
Get-Command git
```

ทั้งสองคำสั่งต้องแสดงข้อมูล Git หากยังไม่พบ ให้ปิด terminal ทุกหน้าต่างแล้วเปิดใหม่ หรือ restart Windows หนึ่งครั้ง

#### B2. Clone branch งานลงเครื่องครั้งแรก

เปิด PowerShell แล้วรันทีละบรรทัด:

```powershell
cd $HOME\Documents
git clone --branch pao01_AI --single-branch https://github.com/b3amkawi/BC-LocalAI.git
cd .\BC-LocalAI
git branch --show-current
git remote -v
git status
```

ผลที่ต้องเห็น:

- `git branch --show-current` แสดง `pao01_AI`
- `git remote -v` แสดง URL `https://github.com/b3amkawi/BC-LocalAI.git`
- `git status` แสดงว่าอยู่ branch `pao01_AI` และไม่มีไฟล์เปลี่ยนแปลง

หาก GitHub เปิดหน้าต่างเบราว์เซอร์ให้ยืนยันตัวตน ให้ลงชื่อเข้าใช้บัญชีที่ได้รับสิทธิ์ Repository ห้ามใส่รหัสผ่าน, token หรือข้อมูลลับลงในไฟล์ของโปรเจกต์

ถ้าขึ้น `destination path 'BC-LocalAI' already exists and is not an empty directory` แปลว่ามีโฟลเดอร์นี้อยู่แล้ว อย่า clone ทับ ให้เข้าโฟลเดอร์เดิมและใช้ขั้นตอนดึงอัปเดตด้านล่าง หรือเลือกชื่อโฟลเดอร์ใหม่:

```powershell
git clone --branch pao01_AI --single-branch https://github.com/b3amkawi/BC-LocalAI.git BC-LocalAI-pao01_AI
cd .\BC-LocalAI-pao01_AI
```

#### B3. ดึงงานล่าสุดในครั้งต่อไป

การ clone ทำเพียงครั้งแรก ครั้งต่อไปให้เปิด PowerShell ในโฟลเดอร์ `BC-LocalAI` แล้วตรวจว่าไม่มีงานของตนเองที่ยังไม่บันทึก:

```powershell
git status --short
git branch --show-current
```

บรรทัดแรกควรว่าง และบรรทัดที่สองต้องเป็น `pao01_AI` จากนั้นดึงงานล่าสุดแบบไม่สร้าง merge commit อัตโนมัติ:

```powershell
git pull --ff-only origin pao01_AI
```

ถ้าขึ้น `Already up to date.` แปลว่ามีไฟล์ล่าสุดแล้ว หาก `git status --short` แสดงชื่อไฟล์ หรือ `git pull --ff-only` ปฏิเสธเพราะ branch แยกจาก remote ให้หยุดและขอผู้ดูแลทีมช่วยตรวจ อย่าใช้ `git reset --hard`, ลบไฟล์ หรือแก้ conflict เอง เพราะอาจทำให้งานที่ยังไม่ได้บันทึกหาย

การตั้ง `user.name` และ `user.email` ยังไม่จำเป็นสำหรับการ clone หรือ pull ต้องตั้งเมื่อได้รับมอบหมายให้ commit เท่านั้น คู่มือนี้ไม่ให้ push หรือ merge เข้า `main`

### เปิด PowerShell ภายในโฟลเดอร์ที่ดาวน์โหลด

1. เปิดโฟลเดอร์ `BC-LocalAI-pao01_AI` จากวิธี ZIP หรือ `BC-LocalAI` จากวิธี Git ใน File Explorer
2. ตรวจว่ามองเห็น `README.md`, `docs` และ `scripts`
3. คลิกช่อง address bar ด้านบนของ File Explorer
4. พิมพ์ `powershell` แล้วกด Enter
5. PowerShell ที่เปิดขึ้นมาต้องมีตำแหน่งท้าย prompt เป็นชื่อโฟลเดอร์ดังกล่าว

ตรวจตำแหน่งและไฟล์ที่จำเป็น:

```powershell
Get-Location
Test-Path .\scripts\test-ollama.ps1
Test-Path .\scripts\open-chat.cmd
Test-Path .\docs\ai\ollama-api.md
```

คำสั่ง `Test-Path` ทั้งสามบรรทัดต้องเป็น `True` หากมีบรรทัดใดเป็น `False` ให้หยุดก่อน เพราะอาจดาวน์โหลดผิด branch, ยังไม่ได้แตก ZIP หรือเปิดผิดโฟลเดอร์ หากทราบตำแหน่งที่ถูกต้อง ใช้ `cd` เช่น:

```powershell
cd "$HOME\Downloads\BC-LocalAI-pao01_AI"
Test-Path .\scripts\test-ollama.ps1
```

เมื่อได้ `True` จึงไปข้อ 7

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

หาก `pwsh is not recognized` ให้ติดตั้งตาม [คู่มือ PowerShell บน Windows ของ Microsoft](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) แล้วเปิด terminal ใหม่ `powershell` และ `pwsh` เป็นคนละโปรแกรม: Windows PowerShell เดิมใช้ `powershell`; PowerShell 7 ใช้ `pwsh` หลังติดตั้งต้องตรวจ `pwsh --version` ให้สำเร็จก่อนรันสคริปต์

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
| `git is not recognized` | ยังไม่มี Git หรือ terminal ยังไม่โหลด PATH | ติดตั้ง Git for Windows แล้วเปิด terminal ใหม่ |
| Clone ขึ้น `Repository not found` | ยังไม่ได้เข้าสู่ระบบหรือไม่มีสิทธิ์ Repository | เปิด Repository ในเบราว์เซอร์ด้วยบัญชีที่ได้รับสิทธิ์ก่อน |
| Clone ขึ้น `destination path ... already exists` | มีโฟลเดอร์ชื่อเดียวกันอยู่แล้ว | เข้าโฟลเดอร์เดิมแล้ว pull หรือ clone โดยกำหนดชื่อโฟลเดอร์ใหม่ |
| `ollama is not recognized` | terminal ยังไม่โหลด PATH หรือติดตั้งไม่สำเร็จ | เปิด terminal ใหม่ เปิด Ollama แล้วลอง `Get-Command ollama` |
| `pwsh is not recognized` | ไม่มี PowerShell 7 | ติดตั้ง PowerShell 7 ตามคู่มือ Microsoft |
| ต่อ `127.0.0.1:11434` ไม่ได้ | Ollama ยังไม่เปิด | เปิด Ollama หรือรัน `ollama serve` ในอีกหน้าต่าง |
| HTTP 404 / ไม่พบโมเดล | ยังไม่มี `qwen3:4b` | รัน `ollama pull qwen3:4b` แล้วตรวจ `ollama list` |
| ตอบช้าหรือ timeout | โหลดครั้งแรกหรือ RAM/VRAM ไม่พอ | ปิดงานที่ใช้ GPU แล้วทดสอบด้วย `-TimeoutSec 300` |
| คำถามกลายเป็น error สีแดง | พิมพ์ที่ prompt `PS ...>` | เปิด `scripts\open-chat.cmd` แล้วรอ prompt `คุณ:` |
| port 11434 ถูกใช้งานแล้ว | Ollama ทำงานอยู่แล้ว | ไม่ต้องเปิด `ollama serve` ซ้ำ ให้ทดสอบ API ต่อ |

## 11. Checklist สำหรับยืนยันบนเครื่องใหม่

การติดตั้งถือว่าผ่านเมื่อทำครบและเห็นผลตามนี้:

- [ ] `ollama --version` แสดงเวอร์ชัน
- [ ] `Invoke-RestMethod http://127.0.0.1:11434/api/version` คืนข้อมูลเวอร์ชัน
- [ ] `ollama list` มี `qwen3:4b`
- [ ] `ollama run qwen3:4b` รับคำถามและตอบกลับ แล้วออกด้วย `/bye` ได้
- [ ] วิธี Git: `git --version` สำเร็จ, branch เป็น `pao01_AI` และ `git status` ไม่มีไฟล์เปลี่ยนแปลง หรือวิธี ZIP: แตกไฟล์จาก branch `pao01_AI` แล้ว
- [ ] `Test-Path` ทั้งสามไฟล์ได้ `True`
- [ ] `pwsh --version` แสดงเวอร์ชัน PowerShell 7
- [ ] `pwsh -NoProfile -File .\scripts\test-ollama.ps1` แสดง `PASS` และ `$LASTEXITCODE` เป็น `0`
- [ ] `pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435` แสดง error ที่เข้าใจได้ และ `$LASTEXITCODE` เป็น `1`
- [ ] `.\scripts\open-chat.cmd` เปิดได้ เห็น prompt `คุณ:` และตอบคำถามได้

บันทึก Windows, RAM, GPU/VRAM, เวอร์ชัน Ollama, เวอร์ชัน PowerShell และผล `PASS` ของเครื่องใหม่ไว้เป็นหลักฐานด้วย หากข้อใดไม่ผ่าน ให้บันทึกข้อความ error ตามจริงและแก้ตามตารางก่อน ยังไม่ควรสรุปว่าติดตั้งสำเร็จเพียงเพราะเปิดโปรแกรมได้

อย่าเปลี่ยนโมเดลเพื่ออ้างว่า Issue ผ่าน เพราะเกณฑ์ระบุ `qwen3:4b` ดู [คู่มือ API](ollama-api.md) และ [หลักฐานการทดสอบ](ollama-test-evidence.md) เพิ่มเติม ผลจาก Windows 11 เป็นหลักฐานของเครื่องทดสอบนี้ ไม่ใช่หลักฐาน Windows Server
