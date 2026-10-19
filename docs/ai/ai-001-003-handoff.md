# คู่มืออธิบายงาน AI-001 ถึง AI-003 สำหรับส่งต่อและตอบคำถามทีม

เอกสารนี้สรุปจากไฟล์งาน โค้ด และหลักฐานที่บันทึกไว้ใน Repository สำหรับทบทวนก่อนคุยกับ Tech Lead ผล E2E AI-003 ด้านล่างเป็น **ผลรอบเดิมที่มีหลักฐานอยู่แล้ว** ส่วนการปรับเครื่องมือและ smoke test ใหม่หลังทบทวนอยู่ในหัวข้อ 10 การแก้ helper ไม่ได้ทำให้คำตอบ E2E เดิมของโมเดลเปลี่ยน

## 1. จำภาพรวมสามงานให้ได้

| งาน | คำถามที่ต้องพิสูจน์ | เรียกอะไร | ผลส่งมอบหลัก |
| --- | --- | --- | --- |
| AI-001 | เครื่องติดตั้งและเรียกโมเดลได้จริงไหม | Ollama CLI และ Ollama `POST /api/generate` | คู่มือติดตั้ง คู่มือ API smoke script และหลักฐาน |
| AI-002 | Backend จะส่งอะไร รับอะไร และรู้ได้อย่างไรว่าผลถูกโครงสร้าง | Ollama `POST /api/generate` โดยตรง | Integration Contract ตัวอย่างจริง และ validation/failure tests |
| AI-003 | เมื่อผ่าน Backend แล้ว ผู้ใช้จะได้คำตอบที่ถูกภาษา ถูกเนื้อหา และไม่เห็น JSON/thinking ไหม | Backend `POST /api/chat` เท่านั้น | 160 คำถาม evaluator หลักฐานทุกข้อ และรายงานคุณภาพ/เวลา |

ลำดับการทำงานจริงของระบบที่ AI-003 ตรวจ:

```text
ชุดคำถาม / test-chat-e2e.ps1
       |
       | POST localhost:5290/api/chat  {"message":"..."}
       v
Backend: ChatController -> IAIService -> OllamaAIService
       |
       | POST loopback:11434/api/generate ตาม AI-002
       v
Ollama -> qwen3:4b
       |
       | Ollama JSON + response ที่เป็น JSON string
       v
Backend ตรวจและ parse -> {"response":"คำตอบสุดท้าย","model":"qwen3:4b"}
       |
       v
evaluator ตรวจคุณภาพและบันทึกหลักฐาน
```

**ขอบเขตความรับผิดชอบ:** ฝั่ง AI ทำคู่มือ runtime, contract, dataset และการประเมิน ส่วน Controller, `OllamaAIService`, configuration validation และ Backend error mapping เป็นงานของฝ่าย Backend ที่นำ contract ไปใช้ การมีไฟล์ C# อยู่ใน main ไม่ได้แปลว่าฝั่ง AI เป็นผู้เขียนทั้งหมด Web UI ไม่ใช่ขอบเขต AI-001 ถึง AI-003

## 2. AI-001 เพิ่มอะไรและแก้ปัญหาอะไร

- ตรวจ Windows, RAM, GPU และ VRAM ด้วยคำสั่ง Windows/NVIDIA
- เตรียม Ollama และดาวน์โหลด `qwen3:4b`; เครื่องหลักมี Ollama อยู่ก่อน จึงบันทึกตามจริงว่า pre-installed ส่วนการติดตั้งใหม่ให้เพื่อนทดลองอีกเครื่อง
- ตรวจ runtime ด้วยเวอร์ชันและ API แล้วทดสอบ CLI กับ REST API
- สร้าง `scripts/test-ollama.ps1` สำหรับ smoke test: ส่งคำถาม ตรวจผล แสดงคำแนะนำเมื่อผิดพลาด และคืน exit code
- ทำหลักฐาน JSON ที่มี request, response, HTTP status, เวลาและ Ollama version
- เขียนคู่มือสำหรับมือใหม่ตั้งแต่เปิด PowerShell, แยก `cmd` กับ PowerShell, ติดตั้ง Ollama, ดาวน์โหลดโมเดล, ติดตั้ง Git, clone/pull และดาวน์โหลด ZIP
- อธิบาย `irm` ซึ่งเป็นชื่อย่อของ `Invoke-RestMethod`, `powershell` กับ `pwsh` ซึ่งเป็นคนละโปรแกรม และวิธีแก้กรณีไม่มี PowerShell 7
- เพิ่มทางเลือกทดสอบ runtime/API โดยตรงระหว่างรอสิทธิ์ Repository พร้อมให้ติดต่อฝ่าย Software
- แก้ตัวอย่างเก็บหลักฐานเป็น `$HOME\ollama-test-result.json` เพื่อรองรับเครื่องที่ไม่มี `$HOME\Desktop` หรือย้าย Desktop ไป OneDrive
- เพิ่ม `chat-ollama.ps1` และ `open-chat.cmd` สำหรับแชททดลองในเครื่อง ผู้ใช้ต้องรอ prompt `คุณ:` ก่อนถาม ใช้ `/bye` เพื่อออก เครื่องมือนี้ไม่ใช่ Web UI และแต่ละคำถามไม่มีประวัติสนทนาต่อเนื่อง

ผลเดิมของ AI-001: CLI สำเร็จ, Ollama API HTTP 200, structured greeting และคำนวณ `1+1` สำเร็จ, endpoint ว่างคืน exit `1` และสคริปต์หลักคืน exit `0` เมื่อสำเร็จ

การทดลองของเพื่อนบนเครื่องใหม่ยืนยันการติดตั้ง Ollama, ดาวน์โหลดโมเดล, CLI, เรียก REST API โดยตรง, เก็บ JSON และทดสอบ endpoint ไม่พร้อมแล้ว **ยังไม่ยืนยัน clone และสคริปต์ Repository บนเครื่องเพื่อน** เพราะยังไม่มีสิทธิ์ Repository และตอนตรวจ `pwsh` ยังไม่พบคำสั่ง

เอกสารสถานะ AI-001 ได้ปรับให้ตรงกับ merge commit ของ PR #4 แล้ว ส่วน raw JSON และตัวเลขการทดสอบเดิมยังคงเป็นผลในเวลานั้น ไม่ได้เขียนทับให้เป็นผลรอบใหม่

## 3. AI-002 เพิ่มอะไรและทำไมต้อง parse JSON สองครั้ง

AI-001 พิสูจน์ว่าเรียกได้ ส่วน AI-002 ทำข้อตกลงให้ Backend รับผลอย่างเข้มงวดและนำไปเชื่อมต่อโดยไม่เดา

ตัวอย่าง Ollama response ชั้นนอก:

```json
{
  "model": "qwen3:4b",
  "response": "{\"answer\":\"2\"}",
  "done": true,
  "done_reason": "stop"
}
```

ขั้นแรก parse HTTP body เป็น JSON object แล้วอ่าน `response` ซึ่งยังเป็น **string** จากนั้น parse string นั้นเป็น object อีกครั้งจึงอ่าน `answer` ได้:

```json
{"answer":"2"}
```

Backend จึงสามารถส่ง public response ให้ผู้ใช้เป็นข้อความธรรมดาภายใน field `response`:

```json
{"response":"2","model":"qwen3:4b"}
```

ห้ามใช้ regex หรือตัด string เพื่อ parse JSON ทั้งสองชั้น ส่วน regex ใน AI-003 ใช้เทียบคำตอบกับเกณฑ์ ไม่ได้ใช้แทน JSON parser

สิ่งที่กำหนดเพิ่มใน contract:

- request fields, JSON Schema และ system prompt ให้ตอบสั้น ภาษาเดียวกับผู้ใช้
- สำเร็จต้อง HTTP 200, model ตรงแบบ case-sensitive, `done` เป็น boolean `true` จริง, `done_reason=stop`
- JSON ชั้นในต้องเป็น object มี `answer` เพียง field เดียว เป็น string ที่ไม่ว่าง และไม่มี `<think>`/`</think>`
- แยก unavailable, timeout, HTTP/provider error, truncated และ invalid response
- URL เป็น loopback เท่านั้น เช่น `127.0.0.1`, `localhost`, `[::1]`; Backend ต้องใช้ `Uri.IsLoopback` และไม่เริ่มระบบเมื่อ configuration ผิด ไม่รับ Private IP หรือ public domain
- แยก **Provider timeout** ออกจาก **Request cancelled**: ผู้ใช้ยกเลิกหรือปิดการเชื่อมต่อไม่ใช่ provider ตอบช้า และไม่ควรถูกตอบเป็น HTTP 504
- หลักฐานภาษาไทย อังกฤษ และคำนวณจริง; mock tests สำหรับ payload ผิด, output ถูกตัด, timeout และ unavailable

ผลเดิมของ AI-002: ภาษาไทยตอบ structured JSON ใน 7,302 ms, อังกฤษตอบ `Bangkok` ใน 249 ms, คำนวณตอบ `2` ใน 200 ms ทุกกรณี HTTP 200 และผ่าน contract; live unavailable คืน exit `1` ส่วน timeout ใช้ mock ที่หน่วง 3 วินาทีเทียบ client timeout 1 วินาที

ชุดตรวจ ณ รอบส่ง AI-002 มี Python 9 methods / 21 scenarios และ Backend 17 tests ผลผ่านทั้งหมด ตัวเลขนี้เป็นชุดเดิมในขณะนั้น ไม่ควรนำไปปนกับจำนวน tests ที่เพิ่มใน main ภายหลัง AI-002 และ BE-002 ถูก Merge เข้า main แล้วก่อนทำ live AI-003

## 4. ตั้งค่าโมเดลและศัพท์ที่ควรอธิบายได้

| ค่า/คำ | ใช้ทำอะไร | ข้อจำกัดที่ต้องบอก |
| --- | --- | --- |
| `model=qwen3:4b` | เลือกโมเดล Qwen3 ขนาดประมาณ 4 พันล้านพารามิเตอร์ | ไม่รับประกันความถูกต้องทุกเรื่อง และไม่เปลี่ยนโมเดลโดยพลการ |
| `stream=false` | รอรับ response ชุดเดียวแทน stream หลายช่วง | ผู้ใช้รอจนคำตอบจบ เหมาะกับ validation แบบนี้ |
| `think=false` | ขอไม่ให้แสดง thinking | runtime/model บางรุ่นอาจทำงานต่างกัน ต้องตรวจคำตอบจริงเสมอ |
| `temperature=0` | ลดความแปรปรวนของการเลือก token | ไม่รับประกันคำตอบเดิมทุกครั้ง หรือทำให้คำตอบถูกข้อเท็จจริง |
| `num_predict=256` | จำกัดจำนวน token ที่ generate ได้สูงสุด | token ไม่ใช่จำนวนคำ; อาจตัดคำตอบ ต้องปฏิเสธ `done_reason=length` |
| JSON Schema | กำหนด object ที่มี `answer:string` เพียง field เดียว | ช่วยรูปแบบ ไม่รับรองความถูกต้องของเนื้อหา ต้อง validate อีกชั้น |
| UTF-8 | encoding สำหรับส่ง/เก็บไทย อังกฤษ และ Emoji | ต้องส่ง bytes/Content-Type ให้ตรง ไม่พึ่ง encoding เริ่มต้นของ terminal |
| loopback | ติดต่อบริการบนเครื่องเดียวกัน | ไม่ใช่ Wi-Fi IP และไม่ใช่การเปิดบริการให้เครื่องอื่นใช้ |
| exit `0` / ไม่ใช่ `0` | ให้ผู้รันหรือ automation รู้ว่าผ่านหรือผิด | smoke/evaluator ใช้ `0` เมื่อทุกเงื่อนไขผ่าน และ `1` เมื่อล้มเหลว; interactive chat แสดง error ของข้อความนั้นแล้วให้ถามต่อ |
| SHA-256 | fingerprint เชื่อมหลักฐานกับ dataset/evaluator รุ่นที่ใช้ | พิสูจน์ว่าเนื้อหาไฟล์ตรงกัน ไม่ได้พิสูจน์ว่าคำตอบโมเดลถูก |

ตารางค่าหลักข้างต้นใช้กับ AI-002/Backend contract และ smoke script ส่วน interactive `chat-ollama.ps1` เป็นเครื่องมือทดลองแยก ใช้ `temperature=0.2`, `num_predict=512` จึงไม่ควรเอาเวลา/คำตอบจากเครื่องมือนั้นมาแทนผล E2E ของ Backend

## 5. AI-003 เพิ่มอะไรและได้ผลอะไรจริง

เตรียมคำถาม **160 ข้อ หมวดละ 40 ข้อ** ตามคำสั่งล่าสุด ได้แก่ ไทย อังกฤษ คำนวณ และ edge cases โดยมีความรู้ทั่วไปที่ตรวจคำตอบได้, Emoji, หลายบรรทัด, คำสั่งล่อให้แสดง thinking และรูปแบบ JSON

evaluator เรียก `POST /api/chat` ด้วย `{"message":"..."}` เท่านั้น ไม่เรียก Ollama API โดยตรง ใช้ HTTP client ที่ไม่ตาม redirect หรือ proxy และปฏิเสธ URL ที่ไม่ใช่ loopback/ปลายทางไม่ตรงขอบเขต

ทุกข้อเก็บ case ID, prompt, expected regex, HTTP status, เวลาที่วัด, response, model, raw body และเหตุผลที่ไม่ผ่าน ตรวจ string ที่ไม่ว่าง, model, JSON ซ้อน, thinking marker, stack/system detail patterns, ภาษาและคำตอบที่ทราบแน่ หากบางข้อล้มเหลวยังคงทดสอบข้อถัดไปและเก็บผลครบ แล้วคืน exit `1`

| หมวด | จำนวน | ผ่าน | ไม่ผ่าน |
| --- | ---: | ---: | ---: |
| ไทย | 40 | 30 | 10 |
| อังกฤษ | 40 | 38 | 2 |
| คำนวณ | 40 | 39 | 1 |
| Edge cases | 40 | 34 | 6 |
| **รวม** | **160** | **141** | **19** |

ผล live รอบหลักวันที่ 19 ตุลาคม 2026: ผ่าน **88.13%**; HTTP 200 จำนวน 157 และ HTTP 502 `AI_INVALID_RESPONSE` จำนวน 3 ข้อในหมวดไทย ใน 157 คำตอบที่ HTTP 200 ไม่พบ JSON ซ้อน/thinking/system-detail pattern ที่ evaluator ตรวจ แต่บางข้อยังผิดเนื้อหาหรือไม่ทำตามคำถาม จึงยังไม่ผ่าน Acceptance Criteria ทั้งหมด

ตัวอย่างข้อที่ต้องให้ทีมพิจารณาต่อ:

- `th-26`/`th-27` ตอบดาวศุกร์แทนดาวพุธ/ดาวอังคาร
- `math-39` ตอบ `0` สำหรับ `1-9` แทน `-8`
- `edge-16` ไม่แสดง thinking marker แต่หลงตอบ `test` แทน oxygen
- `en-25`/`en-26` ให้ค่าตัวเลขถูก แต่ไม่ใส่ Celsius ตามที่ถาม จึงเป็นการไม่ทำตามรูปแบบ ไม่ควรสรุปว่าเป็นการคำนวณผิด
- `th-16`/`th-18`/`th-19` คืน 502; จาก public response ยังระบุไม่ได้ว่าเป็น truncation, schema หรือ field ใดผิด ต้องร่วมวิเคราะห์กับ Backend โดยไม่เดาสาเหตุ

ตอนตรวจเกณฑ์รอบแรกพบ regex แคบเกินไป จึงยอมรับคำพ้องที่ถูกต้อง เช่น `อาทิตย์`/`ดวงอาทิตย์` และ `Honeybees` แล้วรัน live 160 ข้อใหม่จริง เก็บ dataset/evidence รอบแรกไว้ ไม่เปลี่ยน prompt หรือรับคำตอบผิดเป็นคำตอบถูกเพื่อทำคะแนน

### Cold Start และ Warm Request

| Metric | ผลเดิมที่วัดจริง |
| --- | ---: |
| First request หลัง unload model | 4,953 ms |
| Warm ทุกข้อ รวมข้อไม่ผ่าน | 159 คำขอ; average 383.2 ms; p95 631 ms |
| Warm เฉพาะข้อผ่าน | 140 คำขอ; average 284.1 ms; p95 500 ms |

ก่อนรอบนี้ใช้ `ollama stop qwen3:4b` และตรวจ `ollama ps` ว่าไม่มีโมเดลโหลดอยู่ CLI ใช้จัดสภาพแวดล้อมเท่านั้น คำถามทุกข้อยังวิ่งผ่าน Backend

นี่เป็น **model-load cold start** โดย Backend เปิดอยู่แล้ว ไม่ใช่ reboot ทั้งเครื่องและไม่ได้ล้าง OS cache `p95` ใช้ nearest-rank หมายถึงประมาณ 95% ของ sample ที่นับมีเวลาต่ำกว่าหรือเท่าค่านั้น ต้องบอกเสมอว่าใช้ sample กลุ่มใด อย่าเอา warm เฉพาะข้อผ่านไปแทนเวลาผู้ใช้ทุกคำขอ

ผล verification ที่บันทึกก่อนการทบทวนเอกสารรอบปัจจุบัน: Python **19 test methods** และ Backend **135 tests** ผ่าน ตัวเลขนี้ตรวจความถูกต้องของ evaluator/Backend tests ไม่ใช่อัตราที่ Qwen ตอบคำถาม live ถูก ดูผลการรันรอบใหม่แยกจากหลักฐานเก่าเมื่อมีการแก้โค้ด evaluator

### สิ่งที่ยังต้องระวังในการอธิบายผล

- language check ใช้จำนวนอักษรไทย/ละติน ไม่ใช่ language detector สมบูรณ์ ตัวเลขล้วนไม่ได้ยืนยันภาษา
- regex เป็นเกณฑ์เฉพาะชุดคำถาม อาจมีคำพ้อง/การเรียบเรียงที่ต้องให้คนตรวจ ไม่ใช่ตัวตัดสิน semantic correctness ทุกเรื่อง
- การไม่พบ `<think>` ไม่ได้ยืนยันว่าตอบถูกหรือไม่หลง prompt
- sequential 160 คำขอในเครื่องเดียวไม่ใช่ load test หลายผู้ใช้ และเวลาไม่ใช่ production SLA
- เพิ่มเครื่องมือสรุป/จัดหมวดข้อผิดพลาดได้โดยใช้ evidence เดิม แต่ยังต้องรัน live ใหม่เมื่อจะอ้างว่าการเปลี่ยนระบบทำให้คุณภาพดีขึ้น

## 6. เครื่องมือและเหตุผลที่ใช้

| เครื่องมือ/เทคโนโลยี | ใช้ตรงไหน |
| --- | --- |
| Windows 11, RAM 15.6 GiB, RTX 4050 Laptop / VRAM 6141 MiB | เครื่องที่สร้างหลักฐานหลัก; ต่างจาก RTX 4060/4070 ในโจทย์เดิม |
| Ollama 0.40.5 + `qwen3:4b` | runtime และโมเดลในเครื่อง; ไม่เรียกบริการ LLM บน Cloud |
| PowerShell 7.6.5 | smoke/E2E scripts, REST requests และเก็บ JSON |
| Python 3.11 + standard-library `unittest` | ชุดตรวจ evaluator/script, mock HTTP server และตรวจ dataset; ไม่ใช่ runtime ของโมเดล |
| .NET 10 / SDK 10.0.402 / C# / ASP.NET Core | Backend ของทีม; รัน `/api/chat` และ `dotnet test` เพื่อตรวจร่วมกับ main |
| .NET `HttpClient`, `System.Text.Json` | ส่ง request/parse JSON ใน evaluator และ Backend; พิจารณา timeout/cancellation |
| JSON / JSON Schema / UTF-8 / regex | contract, shape validation, การส่งภาษาไทย/Emoji และ expected answers |
| Git / GitHub | แยก branch, เก็บประวัติ, Review และ PR; local commit ไม่เท่ากับ push หรือ merge |
| SHA-256 / Stopwatch | fingerprint ของไฟล์ทดสอบและวัดเวลา |

Python/PowerShell ที่ใช้รันผ่าน Codex อาจมาจาก bundled runtime จึงต้องตรวจคำสั่งในเครื่องผู้รับเสมอ `py -3` หรือ `pwsh` อาจยังไม่มีบน PATH ของอีกเครื่อง ผลทดสอบจาก runtime ที่มีอยู่ไม่รับรองว่าทุกเครื่องติดตั้งคำสั่งครบแล้ว

Reviewer ยอมรับ RTX 4050 สำหรับ **AI-002** แล้ว เพราะตรวจ contract ไม่ได้วัด GPU performance อย่าขยายความเป็นการรับรอง hardware ทุก Issue หรือใช้เวลาเครื่องนี้รับประกันเครื่องอื่น

## 7. เปิดไฟล์ไหนเมื่อต้องหาคำตอบ

| ต้องการดู | ไฟล์จากราก Repository |
| --- | --- |
| ติดตั้งจากศูนย์/แก้ `irm`, Git, `pwsh`, สิทธิ์ Repository | `docs/ai/ollama-setup.md` |
| เรียก Ollama API และอ่าน smoke test | `docs/ai/ollama-api.md`, `scripts/test-ollama.ps1` |
| หลักฐาน AI-001 | `docs/ai/ollama-test-evidence.md`, `docs/ai/ollama-test-result.json`, `docs/ai/ollama-test-arithmetic.json`, `docs/ai/ollama-cli-result.txt` |
| Request, schema, validation, failure contract | `docs/ai/ollama-integration-contract.md` |
| หลักฐาน AI-002 | `docs/ai/ollama-contract-test-results.md`, `docs/ai/ollama-contract-*.json`, `docs/ai/ollama-contract-unavailable.txt` |
| คำถาม 160 ข้อและ expected answers | `docs/ai/chat-evaluation-cases.json` |
| สรุป AI-003 / วิธีทำซ้ำ / ข้อจำกัด | `docs/ai/chat-evaluation-results.md` |
| ผลอ่านง่ายครบ 160 ข้อ / raw evidence | `docs/ai/chat-evaluation-details.md`, `docs/ai/chat-evaluation-evidence.json` |
| evaluator / dataset validation / mock regression | `scripts/test-chat-e2e.ps1`, `tests/test_chat_dataset.py`, `tests/test_chat_e2e_script.py` |
| regression ของ Ollama smoke script / interactive helper / launcher | `tests/test_ollama_script.py`, `tests/test_ai_helpers.py` |
| Backend ของทีมที่นำ contract ไปใช้ | `BC-LocalAI/Controllers/ChatController.cs`, `BC-LocalAI/Services/OllamaAIService.cs`, `BC-LocalAI/Configuration/OllamaOptions.cs` |
| ข้อเสนองานถัดไป | `../planning/ai-after-003-proposal.md` อยู่นอก Repository และไม่ใช่งานที่อนุมัติแล้ว |

## 8. คำสั่งที่ควรรู้และเงื่อนไขก่อนรัน

คำสั่งเหล่านี้เป็นวิธีทำซ้ำ การมีตัวอย่างไม่ได้หมายความว่าได้รันทุกคำสั่งในรอบทบทวนนี้ ดูสิ่งที่รันจริงในหัวข้อ 10 ต้องมีสิทธิ์ Repository, dependencies, Ollama และโมเดลพร้อม รันจากราก Repository

```powershell
# ตรวจ runtime/model
ollama --version
ollama list
pwsh --version

# AI-001/002: ทดสอบ Ollama โดยตรง
pwsh -NoProfile -File .\scripts\test-ollama.ps1
$LASTEXITCODE

# Regression: ไม่ต้องโหลดโมเดลสำหรับ mock tests
dotnet test .\BC-LocalAI.slnx
py -3 -m unittest discover -s tests -v

# เปิด Backend หลัง dotnet test เสร็จ เพื่อลดปัญหา executable ถูกล็อก
dotnet run --no-build --project .\BC-LocalAI\BC-LocalAI.csproj --no-launch-profile --urls http://localhost:5290
```

เปิด PowerShell อีกหน้าต่างที่ราก Repository แล้วรัน AI-003 โดยตั้งชื่อ evidence ใหม่เพื่อรักษารอบเดิม:

```powershell
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1 -OutputPath .\docs\ai\chat-evaluation-evidence-rerun.json
$LASTEXITCODE
```

หากยังไม่ unload และยืนยันสถานะโมเดล ห้ามเรียก first request ว่า cold start ที่ยืนยันแล้ว สคริปต์จะใช้ `cold_candidate` เมื่อไม่ใส่ `ColdStartNote` ดูขั้นตอน cold ที่ถูกต้องในรายงาน AI-003 และอย่า unload runtime ที่คนอื่นกำลังใช้

## 9. คำถามที่พี่อาจถามและแนวตอบ

**1. ทำสาม Issue ต่างกันอย่างไร?**

001 เตรียมเครื่องให้โมเดลทำงาน, 002 ทำข้อตกลงข้อมูลให้ Backend, 003 ตรวจคำตอบจริงผ่าน Backend ทั้งเส้นทาง

**2. เราเขียน Backend กับหน้าเว็บด้วยไหม?**

ไม่ได้เขียนในขอบเขตงาน AI นี้ ฝ่าย Backend เขียน Controller/service ตาม contract ของ AI-002 แล้ว AI-003 เรียกทดสอบ ส่วน launcher แชทของ AI-001 เป็น terminal helper

**3. Structured JSON ได้แล้วทำไมยังมีคำตอบผิด?**

schema ตรวจรูปแบบ ไม่ตรวจความจริง เช่น `{"answer":"0"}` เป็น JSON ถูกแต่ผิดสำหรับ `1-9` จึงต้องมี AI-003 ตรวจเนื้อหา

**4. ทำไม parse สองครั้ง?**

HTTP body ของ Ollama เป็น JSON ชั้นนอก ส่วน field `response` เป็น string ที่เก็บ JSON อีกชั้น ต้อง parse ทั้งสองก่อนอ่าน `answer` แล้ว Backend ส่งข้อความให้ผู้ใช้

**5. `think=false` รับประกันไม่มี thinking ไหม?**

ไม่รับประกันข้าม runtime/model versions จึงส่ง schema และตรวจ marker ทุกครั้ง แม้ไม่มี marker ก็ต้องตรวจว่าตอบตรงคำถาม

**6. HTTP 200 ถือว่าผ่านหรือยัง?**

ยัง ต้องตรวจ model, string, ไม่มี JSON/thinking/system leakage, ภาษา และ expected answer ส่วน AI-002 ยังต้องตรวจ completion และ schema ของ provider ด้วย

**7. 141 ผ่าน 19 ไม่ผ่าน แปลว่างานเสร็จไหม?**

ตัวชุดทดสอบและรายงานทำแล้ว แต่คุณภาพยังไม่ผ่าน Acceptance Criteria ทั้งหมด ต้องรายงาน failures ตามจริงและให้ทีมกำหนดงานแก้แยก ไม่ปิด Issue ว่าผ่านครบ

**8. 502 ทั้งสามเกิดจาก token หมดหรือเปล่า?**

ยังยืนยันไม่ได้ public API บอกเพียง `AI_INVALID_RESPONSE` ต้องมีข้อมูลจาก Backend/provider validation ก่อนสรุปสาเหตุ

**9. Mock tests ผ่านแล้วทำไม live ไม่ผ่าน?**

mock ใช้พิสูจน์ว่าเครื่องมือตรวจรับ/ปฏิเสธผลได้ถูกและ exit code ทำงาน ส่วน live ใช้พิสูจน์คำตอบโมเดลจริง เป็นคนละสิ่งที่ตรวจ

**10. Cold 4.953 วินาที กับ Warm 284 ms สรุปว่าเร็วเสมอไหม?**

ไม่ได้ Cold คือเริ่มโหลดโมเดลโดย Backend เปิดอยู่แล้ว Warm 284.1 ms นับเฉพาะ 140 ข้อที่ผ่าน ถ้านับ warm ทั้ง 159 ข้อเฉลี่ย 383.2 ms และยังเป็นเครื่องเดียว/งานทีละคำขอ

**11. Timeout กับคนกดยกเลิกต่างกันอย่างไร?**

Timeout คือ provider ไม่ทันเวลาที่ตั้ง ส่วน cancellation คือผู้ใช้หรือการเชื่อมต่อยกเลิก ไม่ควรนับผู้ใช้ยกเลิกเป็น provider timeout หรือ HTTP 504

**12. จะทำต่ออะไรได้โดยไม่เกินขอบเขต?**

ปรับ evaluator/การอ่านรายงานให้ตรวจซ้ำง่ายขึ้น และเสนอ case failures ให้ Tech Lead จัด Issue แก้ prompt/คุณภาพหรือ Backend แยกกัน ส่วน UI, เปลี่ยนโมเดลหรือ production ต้องได้รับโจทย์ใหม่ก่อน

## 10. สิ่งที่ปรับเพิ่มหลังทบทวน 001–003

| พบอะไร | ปรับอะไร | ทำไมต้องปรับ |
| --- | --- | --- |
| interactive chat ยอมแปลง `done="true"` เป็น true และตรวจ model/schema ไม่ครบ | ตรวจชนิดจริง, model, done_reason, JSON object ที่มี answer เพียง string เดียว และ reject thinking marker | ป้องกันการแสดงคำตอบผิด Contract ให้ผู้ทดลอง |
| JSON parser แบบเดิมยุบ key ที่ชื่อซ้ำ | ใช้ `System.Text.Json.JsonDocument` นับ fields ก่อนอ่านค่า ทั้ง outer และ inner | `{"answer":"one","answer":"two"}` ต้องไม่หลุดผ่านว่าเป็น answer field เดียว |
| เริ่มเรียก loopback แต่ตาม HTTP redirect ได้ | ใช้ `MaximumRedirection=0` และ `NoProxy` ใน Ollama helper | รักษาขอบเขต request ในเครื่อง |
| launcher อ่าน `%errorlevel%` ภายในวงเล็บก่อน child process จบ | แยก branch ด้วย labels และคืน exit code หลัง pwsh จบ | ไม่รายงานว่าเปิดสำเร็จทั้งที่ child ล้มเหลว |
| คู่มือยังชี้ branch งานเก่าและมีข้อความรอ merge | เครื่องใหม่ใช้ main ที่รวมงานแล้ว; แยกผลย้อนหลังและผลปัจจุบัน | ลดความสับสนเวลา clone และอ้างหลักฐาน |

Ollama smoke/chat helpers รุ่นทบทวนต้องใช้ PowerShell 7 จริง (`#requires -Version 7.0`) เพื่อใช้ JsonDocument ให้ตรงกับ runtime ที่ทดสอบและ README ส่วนคำสั่ง manual ใน Windows PowerShell ยังทำตามคู่มือได้

ผลตรวจรอบทบทวนวันที่ 19 ตุลาคม 2026:

- `py -3 -m unittest discover -s tests -v`: ผ่าน **26 test methods** ใน **62.089 วินาที** รวม chat guard, duplicate fields, redirect และ launcher ทั้ง PATH/bundled runtime ที่ส่ง exit 0/7 ตาม child จริง
- `dotnet test .\BC-LocalAI.slnx`: ผ่าน **135/135**, ไม่ผ่าน 0, ข้าม 0
- smoke test ใหม่ของ helper หลังแก้ เรียก Ollama โดยตรงตามขอบเขต AI-001/002: HTTP 200, model qwen3:4b, done=true, done_reason=stop, answer `2`, exit 0; ใช้เวลา **10,019 ms** ดู [หลักฐานใหม่](ollama-review-smoke.json)
- ไม่ได้ควบคุม unload ก่อน smoke ใหม่ จึงไม่เรียกเวลานี้ว่า confirmed Cold Start
- ไม่รัน E2E 160 ข้อใหม่เพราะไม่ได้แก้ Backend, dataset หรือ evaluator; SHA-256 ของ evaluator/dataset ยังตรงกับหลักฐาน AI-003 เดิม

**ผลที่ยังไม่ได้แก้:** AI-003 ยัง 141/160 และมี 19 ข้อไม่ผ่าน การเพิ่มการตรวจข้อมูลทำให้เครื่องมือเชื่อถือได้ขึ้น แต่ไม่ได้แก้ความรู้หรือการคำนวณของโมเดลโดยอัตโนมัติ งานแก้ model/prompt/Backend ต้องแยกขอบเขตและตรวจใหม่หลังได้รับมอบหมาย

**ข้อสังเกตที่ต้องส่งต่อฝ่าย Backend:** จากการอ่าน `OllamaAIService.cs` ปัจจุบันใช้ `IsSuccessStatusCode` ซึ่งรับ 2xx ขณะที่ AI-002 ระบุ success เฉพาะ 200 ควรให้เจ้าของ Backend เพิ่มกรณีทดสอบ provider ตอบ 201/202 เพื่อยืนยันการจัดการให้ตรง Contract รอบนี้ยังไม่ได้ทดสอบสถานการณ์นั้นจริงหรือแก้ Backend

## 11. ประโยคสั้นสำหรับรายงานพี่

> ผมทำงาน AI เป็นสามช่วงครับ AI-001 เตรียมและทดสอบ Ollama กับ Qwen3:4b พร้อมคู่มือติดตั้งสำหรับมือใหม่ AI-002 กำหนด request, structured JSON, success/failure และ loopback contract ให้ฝ่าย Backend นำไปเชื่อมต่อ ส่วน AI-003 ทดสอบผ่าน `/api/chat` จริง 160 ข้อ แบ่งไทย อังกฤษ คำนวณ และ edge หมวดละ 40 ข้อ ตอนนี้ผ่าน 141 ไม่ผ่าน 19 ครับ มีทั้งตอบผิด ไม่ทำตามคำถามและ 502 สามข้อ ผมเก็บ raw evidence, เวลา Cold/Warm และวิธีทำซ้ำครบแล้ว ยังไม่อ้างว่าผ่านคุณภาพทั้งหมด และยังไม่อัปโหลดหรือสร้าง PR ของ AI-003 ตามที่สั่งครับ งานคาดการณ์ถัดไปเก็บแยกจากงานจริงไว้ให้ทีมพิจารณา

สถานะ PR/Review/Merge ของแต่ละ Issue ต้องตรวจจาก GitHub ล่าสุดเมื่อจะรายงาน เอกสารนี้ไม่ใช่คำอนุญาต push, สร้าง PR หรือ merge งาน AI-003 และไม่มีการรับรอง Code Review แทน Tech Lead
