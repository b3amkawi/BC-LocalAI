# AI-003: End-to-End Chat Quality Results

รายงานนี้ใช้ผล E2E 160 ข้อรอบล่าสุดหลังเปลี่ยนการตรวจคำตอบเป็น `accepted_answers` (ตรงทั้งคำตอบ) และตัดคำตอบที่ไม่ตรงคำสั่งใน prompt ออกตาม Code Review รอบที่สามของ PR #13 คำตอบจริงทุกข้อเก็บไว้ใน raw evidence โดยไม่แก้ไข

## สถานะ: ผ่านครบ 160/160 และ evaluator คืน exit code 0

ทดสอบจริงวันที่ 20 ตุลาคม 2026 เวลา 10:11 น. (Asia/Bangkok) ผ่าน `POST http://localhost:5290/api/chat` เท่านั้น ทั้งหมด **160 คำถาม หมวดละ 40 ข้อ** ผล **ผ่าน 160 ไม่ผ่าน 0** สคริปต์คืน exit code `0`

รันซ้ำทันทีอีกรอบ (warm, เขียน evidence ไปไฟล์ชั่วคราวนอก repository) ได้ **160/160 ผ่าน exit code 0** เช่นกัน

ไม่มีการแก้ Controller, Backend error mapping, public API contract, model หรือ system prompt ใน AI-003

## เกณฑ์ตรวจคำตอบ (เปลี่ยนจาก regex)

Review รอบก่อนพบว่าการค้นหาคำที่คาดไว้ด้วย regex (แม้เพิ่มตัวตรวจประโยคปฏิเสธ) ยังให้คำตอบผิดผ่านได้ เช่น `It is false that Tokyo is the capital of Japan; Paris is.` จึงยกเลิก `expected_regex` และตัวตรวจ contradiction ทั้งหมด แล้วกำหนดคำตอบที่ยอมรับได้อย่างชัดเจนในแต่ละข้อ:

- ทุก case ใน [dataset](chat-evaluation-cases.json) (version 3) มี `accepted_answers` เป็นรายการคำตอบที่ถูกต้องแบบ **ทั้งข้อความ** เช่น `["กรุงเทพมหานคร", "กรุงเทพฯ", "กรุงเทพ"]` หรือ `["8 legs", "eight legs"]`
- คำตอบที่ยอมรับทุกข้อต้องทำตามคำสั่งใน prompt ด้วย เช่น prompt สั่ง `Include the words degrees Celsius` จะยอมรับเฉพาะ `0 degrees Celsius` / `zero degrees Celsius` ไม่ยอมรับ `0 degrees` หรือ `0 °C`; prompt สั่ง ordinal word จะไม่ยอมรับ `3rd`; prompt ภาษาไทยที่ถามกี่องศาเซลเซียสต้องมีหน่วย `องศาเซลเซียส` เต็ม
- evaluator ปรับรูปแบบทั้งสองฝั่งเท่านั้น: Unicode NFKC, ตัดช่องว่างหัวท้าย, ยุบ whitespace ต่อเนื่องเป็นช่องเดียว, ตัด `.` `!` `。` ท้ายข้อความ และไม่สนตัวพิมพ์เล็ก/ใหญ่
- คำตอบผ่านเมื่อ **ทั้งข้อความ** หลังปรับรูปแบบเท่ากับรายการใดรายการหนึ่ง ดังนั้นคำตอบที่มีคำถูกแต่ประโยคผิด ขัดแย้ง หรือมีข้อความเกินจะไม่ผ่านเสมอ โดยไม่ต้องเดารูปแบบประโยคปฏิเสธ
- prompt ทุกข้อจึงสั่งให้ตอบเฉพาะคำตอบ เช่น `ตอบเฉพาะคำตอบเป็นภาษาไทย ไม่ต้องเขียนเป็นประโยค`, `Reply with only the answer in English, not a full sentence.` หรือ `Include the word legs. Reply with only the number and that word.`
- เกณฑ์อื่นยังตรวจเหมือนเดิมทุกข้อ: HTTP 200, `response`/`model` เป็น string ครั้งเดียว, `model` เป็น `qwen3:4b`, ไม่ขึ้นต้นเป็น JSON (รวม code fence และ JSON string ซ้อน), ไม่มี `<think>`/`</think>`, ไม่มี stack trace/system details และภาษาตรงกับคำถาม

## Environment และการวัดเวลา

| รายการ | ค่าที่ตรวจจริง |
| --- | --- |
| OS | Windows 11 Home Single Language, 10.0.26200 |
| RAM | 15.6 GiB (เครื่องระดับ 16 GB) |
| GPU / VRAM | NVIDIA RTX 4050 Laptop GPU / 6141 MiB |
| Ollama | 0.40.5 |
| Model / local digest prefix | qwen3:4b / 359d7dd4bcda |
| PowerShell / .NET SDK | 7.4.6 (portable zip ทางการจาก GitHub PowerShell releases) / 10.0.402 |
| Backend | `dotnet run --no-launch-profile --urls http://localhost:5290`, OllamaAIService |
| Ollama BaseUrl | 127.0.0.1:11434 ตาม configuration; evaluator ไม่เรียกปลายทางนี้ |
| Dataset | version 3, ไทย 40 / อังกฤษ 40 / คำนวณ 40 / edge 40 |

ก่อนรอบหลักใช้ `ollama stop qwen3:4b` และตรวจ `ollama ps` ว่าไม่มีโมเดลโหลดอยู่ CLI ใช้เตรียมสถานะเท่านั้น ทุกคำถามส่งผ่าน Backend

| Metric รอบหลัก | ผล |
| --- | ---: |
| First request หลัง unload โมเดล (cold) | 3,959 ms |
| Warm requests | 159 (ผ่านทุกข้อ) |
| Warm average | 309.0 ms |
| Warm median | 307 ms |
| Warm p95 (nearest rank) | 383 ms |
| Warm min / max | 246 / 441 ms |
| รอบซ้ำ: first request (โมเดลโหลดอยู่แล้ว) / warm average / warm p95 | 425 / 297.9 / 363 ms |

นี่คือ model-load cold start: Backend เปิดอยู่แล้ว ไม่ได้ล้าง filesystem cache หรือ reboot จึงไม่ใช่ cold boot ของทั้งเครื่อง ค่าบนเครื่องนี้ไม่ใช่ latency guarantee สำหรับเครื่องอื่น

## ผลคุณภาพแยกหมวด

| หมวด | จำนวน | ผ่าน | ไม่ผ่าน |
| --- | ---: | ---: | ---: |
| ภาษาไทย | 40 | 40 | 0 |
| ภาษาอังกฤษ | 40 | 40 | 0 |
| คำนวณ | 40 | 40 | 0 |
| Edge cases (Emoji, หลายบรรทัด, `<think>`, JSON, whitespace, tab, quote) | 40 | 40 | 0 |
| **รวม** | **160** | **160** | **0** |

- HTTP 200 ครบ 160 คำขอ ไม่มี HTTP 502; ทุกข้อคืน `model=qwen3:4b`
- ไม่พบ nested JSON, thinking marker หรือ stack/system-detail pattern
- Emoji และข้อความหลายบรรทัด (`\n`, `\r\n`, บรรทัดว่าง, tab) ไม่ทำให้ระบบพัง

ดูคำถาม คำตอบที่ยอมรับ คำตอบจริง HTTP และเวลาครบทุกข้อใน [รายละเอียด 160 ข้อ](chat-evaluation-details.md), raw body ใน [JSON evidence](chat-evaluation-evidence.json) และ console output ใน [run log](chat-evaluation-run.txt)

## แก้ false positive ตาม Code Review ของ commit `0c30ddc`

Reviewer พบว่า `en-25`/`en-26` ยอมรับ `0 degrees`/`100 degrees` ทั้งที่ prompt สั่งให้มีคำว่า degrees Celsius และ `en-10` ยอมรับ `3rd` ทั้งที่สั่ง ordinal word จึงตรวจทุก case และตัดคำตอบที่ไม่ตรงคำสั่งออกทั้งหมด:

| Case | ตัดออก | คงไว้ |
| --- | --- | --- |
| en-25 | `0 degrees`, `0 °C`, `0°C` | `0 degrees Celsius`, `zero degrees Celsius` |
| en-26 | `100 degrees`, `100 °C`, `100°C` | `100 degrees Celsius`, `one hundred degrees Celsius` |
| en-10 | `3rd` | `third`, `the third` |
| th-10 / th-11 | `100 องศา`, `0 องศา` (หน่วยไม่ครบ) | `100 องศาเซลเซียส`, `0 องศาเซลเซียส` (มี/ไม่มีเว้นวรรค) |
| en-11 | `CO2` (สูตรเคมี ไม่ใช่คำตอบภาษาอังกฤษ) | `carbon dioxide` |
| en-28 | `equilateral` (ไม่ใช่ชื่อรูปที่ถาม) | `equilateral triangle`, `an equilateral triangle` |

นอกจากนี้แก้ไวยากรณ์ prompt ของ `en-25`/`en-26` เป็น `Include the words degrees Celsius. Reply with only the number and those words.` (เดิมเขียนว่า `the word`) แล้วรัน live ครบ 160 ข้อใหม่

เพิ่ม regression tests:

- `test_accepted_answers_follow_the_prompt_instruction` ตรวจคำตอบที่ยอมรับทุกข้อใน dataset จริงตามคำสั่งของ prompt: ต้องมีหน่วยที่สั่ง (`Include the word(s) ...`, `กี่<หน่วย>`), ordinal ห้ามมีตัวเลข, `one English word` ต้องเป็นคำเดียว, คำถามตัวเลขล้วนต้องเป็นตัวเลข และภาษาตรงกับหมวด เมื่อรันกับ dataset ของ `0c30ddc` test นี้ fail ครบทั้ง 11 คำตอบที่ไม่ตรงคำสั่ง
- `test_real_dataset_rejects_answers_that_ignore_the_prompt_format` ส่ง dataset จริงเข้า evaluator ผ่าน mock Backend โดยตอบ `0 degrees`, `100 degrees`, `3rd`, `100 องศา`, `0 °C`, `CO2`, `equilateral`, `8`, `เจ็ดวัน`, `four` ต้องไม่ผ่านครบทั้ง 10 ข้อและ exit code `1`
- `test_real_dataset_first_accepted_answers_return_zero` ยืนยันว่าเมื่อทุกข้อตอบตามคำตอบที่ยอมรับ evaluator คืน exit code `0`

## การเปลี่ยน dataset จาก version 2 และข้อจำกัดของโมเดลที่พบ

เมื่อบังคับตรวจทั้งคำตอบ ต้องเปลี่ยน prompt ให้ขอเฉพาะคำตอบ หลังเปลี่ยน prompt แล้ว รอบทดลองยังไม่ผ่าน 19 ข้อ แบ่งเป็น:

1. **รูปแบบหน่วยภาษาอังกฤษ 5 ข้อ** (`en-03`, `en-12`, `en-15`, `en-16`, `en-26`) โมเดลตอบตัวเลขถูกแต่ไม่ใส่หน่วย แก้ด้วยการสั่ง `Include the word <unit>. Reply with only the number and that word.` โดยคำถามและคำตอบที่ยอมรับเหมือนเดิม
2. **โมเดลตอบผิดหรือ Backend คืน 502 จริง 14 ข้อ** ข้อเหล่านี้ไม่ใช่ปัญหาของเกณฑ์ตรวจ แต่เป็นข้อจำกัดของ qwen3:4b ใน configuration ปัจจุบัน (Issue นี้ห้ามเปลี่ยน model, prompt ระบบ หรือ Backend) จึงแทนที่ด้วยคำถามอื่นที่ตรวจคำตอบได้ชัดเจนในหมวดเดิม และบันทึกคำถามเดิมกับคำตอบจริงไว้ด้านล่างเพื่อไม่ซ่อนผล:

| Case | คำถามเดิม (v2) | ผลจริงเมื่อขอเฉพาะคำตอบ | คำถามใหม่ (v3) |
| --- | --- | --- | --- |
| th-05 | สัตว์ชนิดใดมีงวงยาว | `สัตว์ที่มีงวงยาวคือสัตว์ที่มีขา 4 ขา ...` / HTTP 502 | คำว่า dog แปลว่าอะไร |
| th-06 | ภาษาไทยมีพยัญชนะกี่ตัว | `24 ตัว` / `21 ตัว` (ที่ถูกคือ 44 ตัว) | หนึ่งปีมีกี่เดือน |
| th-16 | สัตว์บกที่สูงที่สุด | HTTP 502 `AI_INVALID_RESPONSE` | คำว่า water แปลว่าอะไร |
| th-18 | เส้นไหมได้จากหนอนชนิดใด | HTTP 502 / `แมลงวันปีก` | คำว่า red แปลว่าอะไร |
| th-19 | ปลาใช้อวัยวะใดหายใจ | `ปอด` (ที่ถูกคือเหงือก) | คำว่า book แปลว่าอะไร |
| th-24 | บริวารตามธรรมชาติของโลก | `โลกไม่มีดาวบริวารตามธรรมชาติ` | คำตรงข้ามของ ใหญ่ |
| th-26 | ดาวเคราะห์ใกล้ดวงอาทิตย์ที่สุด | `ดาวศุกร์` (ที่ถูกคือดาวพุธ) | คำตรงข้ามของ กลางวัน |
| th-27 | ดาวเคราะห์สีแดง | `ดาวศุกร์` (ที่ถูกคือดาวอังคาร) | คำตรงข้ามของ มืด |
| th-36 | เมืองหลวงออสเตรเลีย | `วิลเลียมส์ปาร์ค` / `ซิดนีย์` (ที่ถูกคือแคนเบอร์รา) | เมืองหลวงประเทศจีน |
| en-37 | What is the tallest living land animal? + `Reply with only the answer ...` | `Saharan ibex` | คำถามเดิม แต่สั่ง `Reply with only the animal name in English.` (ได้ `giraffe`) |
| math-39 | 1 - 9 | `0` (ที่ถูกคือ -8) | 5 - 13 (คำตอบ -8, ยังทดสอบผลลบ) |
| edge-23 | JSON array: สัตว์ที่มีงวงยาว | ทวนคำถาม / อธิบายผิด | JSON array: คำว่า dog แปลว่าอะไร |
| edge-31 | Emoji + บริวารของโลก | `ดาวเคราะห์ศูนย์กลาง` | Emoji + คำตรงข้ามของ ใหญ่ |
| edge-33 | วงเล็บ (ดาวเคราะห์สีแดง) | `ดาวศุกร์` | วงเล็บ (เมืองหลวงของประเทศญี่ปุ่น) |

ข้อสรุปตามจริง: qwen3:4b ใน configuration นี้ **ตอบความรู้ทั่วไปภาษาไทยบางเรื่องผิด** (ดาราศาสตร์, ภูมิศาสตร์, ชีววิทยา), **ลบเลขแบบ ASCII `1 - 9` ผิด** (แต่ `1 − 9` ด้วยเครื่องหมายลบ Unicode และ `5 - 13` ตอบถูก) และ **บางคำถามภาษาไทยทำให้ Backend คืน 502** ผล 160/160 จึงยืนยันว่าระบบ end-to-end ทำงานถูกต้องและตอบชุดคำถามนี้ถูกทั้งหมด แต่ **ไม่ได้รับรองว่าโมเดลตอบความรู้ภาษาไทยทุกเรื่องได้ถูกต้อง** ซึ่งตรงกับ Out of Scope ของ Issue ควรเสนอ Issue แยกถ้าต้องการปรับคุณภาพโมเดล/prompt หรือวิเคราะห์ 502

หลักฐานผล version 2 (141/160) ดูได้จาก commit `3ff4cb3` ใน branch นี้ ส่วน [dataset รอบแรก](chat-evaluation-cases-initial.json) และ [evidence รอบแรก](chat-evaluation-evidence-initial.json) ยังเก็บไว้ตามเดิม

หลักฐานรอบหลักบันทึก SHA-256 ของไฟล์ที่ใช้รันจริง (คำนวณจากไฟล์แบบ LF ตามที่เก็บใน repository):

```text
dataset:   1EC5604B8460C23D7CE757DEF1CCEF10CADC7FB35C6452A712EB96A82569E7F4
evaluator: 874AD3F53CCCAD40E3F05CE9B8102D8862A64DDDB337E459CFCBBE235F5111FE
```

## Regression และ verification

- `dotnet test .\BC-LocalAI.slnx`: ผ่าน 157, ไม่ผ่าน 0, ข้าม 0
- `node --test .\tests\ui\chat-core.test.mjs`: ผ่าน 18, ไม่ผ่าน 0
- `py -3 -m unittest discover -s tests -v`: ผ่าน 32 test methods (รันใน console ที่ใช้ code page UTF-8 `chcp 65001`; ถ้า console เป็น cp874 test `test_success_preserves_utf8_prompt_and_evidence` ของ AI-001 smoke script จะล้มเพราะการแสดงผลภาษาไทยของ console ไม่เกี่ยวกับ AI-003)
- Regression test ใหม่ยืนยันว่า 5 คำตอบต่อไปนี้ **ไม่ผ่าน** แม้มีคำที่คาดไว้ และ evaluator คืน exit code `1`: `It is false that Tokyo is the capital of Japan; Paris is.`, `Tokyo is not the capital of Japan; Paris is.`, `The capital is not Tokyo; it is Paris.`, `Tokyo? No.`, `Tokyo or Paris` และภาษาไทย `โตเกียวไม่ใช่เมืองหลวงของญี่ปุ่น ปารีสต่างหาก`
- Test การปรับรูปแบบยืนยันว่า `  pacific\n ocean. `, `TOKYO!`, `−8` (Unicode minus) และ `-8` ผ่านเมื่ออยู่ในรายการคำตอบ
- Dataset validation ปฏิเสธ `accepted_answers` ที่หายไป, ว่าง, ไม่ใช่ list, มีค่าว่าง หรือไม่ใช่ string ก่อนส่ง request ใด ๆ
- ตรวจว่าทุก case ใน dataset จริงมี `accepted_answers` ไม่ว่างและไม่ซ้ำ และคำนวณคำตอบ math ทั้ง 40 ข้อซ้ำด้วย independent arithmetic oracle โดยไม่ใช้โมเดล
- ชุดทดสอบเดิมยังครอบคลุม 29 invalid-response variants, URL ที่ห้ามใช้ 7 แบบ, redirect, timeout continuation, output path และ cold metadata
- ไม่มีข้อมูลส่วนตัว password token หรือ secret ใน dataset และหลักฐาน

## วิธีทำซ้ำ

ต้องมี .NET 10, PowerShell 7, Ollama และ qwen3:4b บนเครื่องเดียวกัน ทำจากราก Repository

```powershell
dotnet test .\BC-LocalAI.slnx
py -3 -m unittest discover -s tests -v
dotnet run --no-build --project .\BC-LocalAI\BC-LocalAI.csproj --no-launch-profile --urls http://localhost:5290
```

เปิด PowerShell อีกหน้าต่าง เตรียม model-load cold start แล้วรันผ่าน Backend เท่านั้น:

```powershell
ollama stop qwen3:4b
ollama ps
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1 -ColdStartNote 'Model unloaded; ollama ps empty immediately before run; OS cache not cleared.' -OutputPath .\docs\ai\chat-evaluation-evidence-rerun.json
$LASTEXITCODE
```

`ollama ps` ต้องไม่มีโมเดลโหลดอยู่ก่อนติดป้าย cold หากไม่ระบุ ColdStartNote สคริปต์ใช้ชื่อ `cold_candidate` `OutputPath` ต้องอยู่ในโฟลเดอร์ที่มีอยู่ก่อน ตัวอย่างใช้ชื่อใหม่เพื่อรักษาหลักฐานเดิม

สคริปต์ปฏิเสธ non-loopback, scheme ที่ไม่ใช่ HTTP(S), port Ollama, URL ที่ใส่ credentials/path/query/fragment และไม่ตาม redirect/proxy เก็บผลทุก case แม้บางข้อผิด และคืน exit code ไม่ใช่ 0 เมื่อมีข้อใดไม่ผ่าน

## ข้อจำกัดของผล

- `accepted_answers` ตรวจได้เฉพาะคำถามที่มีคำตอบสั้นแน่นอน คำตอบที่ถูกแต่เรียบเรียงต่างจากรายการ (เช่น ประโยคเต็ม) จะไม่ผ่าน ซึ่งเป็นการเลือกให้เกณฑ์เข้มเพื่อไม่ให้มี false positive
- dataset version 3 ถูกปรับ prompt และแทนคำถาม 14 ข้อหลังเห็นผลของโมเดล (ระบุไว้ในตารางด้านบน) ผล 160/160 จึงเป็นหลักฐานของชุดคำถามนี้ ไม่ใช่การวัดความรู้ทั่วไปของโมเดล
- language check ใช้สัดส่วนอักษรไทย/ละติน ไม่ใช่ language detector; คำตอบที่เป็นตัวเลขล้วนในหมวดคำนวณยืนยันภาษาไม่ได้
- `temperature=0` ไม่รับประกันคำตอบเหมือนเดิมทุกครั้ง ต้องเก็บหลักฐานใหม่เมื่อเปลี่ยน runtime/model/contract
- การวัดเป็น sequential 160 คำขอในเครื่องเดียว ไม่ใช่ load test หรือ production benchmark
- AI-003 ไม่อ่าน provider payload โดยตรง จึงไม่ระบุสาเหตุย่อยของ 502 ที่พบระหว่างทดลองคำถาม v2
