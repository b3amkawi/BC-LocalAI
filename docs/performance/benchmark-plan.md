# AI-004: Benchmark Test Plan (D1)

แผนนี้อธิบายวิธีวัดประสิทธิภาพ Ollama + `qwen3:4b` และ Backend `POST /api/chat` ภายใต้คำขอพร้อมกันหลายระดับ เพื่อสร้าง baseline และหาคอขวดก่อนทำ BE-003 (timeout/ความยาวคำตอบ) และ BE-004 (streaming) ผลจริงอยู่ใน [Performance Report](benchmark-report.md)

## 1. Test Environment (ค่าจริงที่ตรวจ ไม่ได้คาดเดา)

| รายการ | ค่า | แหล่งที่มา |
| --- | --- | --- |
| CPU | Intel Core 5 210H, 8 cores / 12 threads, base 2.2 GHz | `Win32_Processor` |
| RAM | 15.6 GiB รวม; ว่าง 2–4 GiB ระหว่างทดสอบ (มีโปรแกรมอื่นเปิดอยู่) | `Win32_OperatingSystem` |
| GPU / VRAM | NVIDIA GeForce RTX 4050 Laptop GPU / 6,141 MiB, driver 617.14 | `nvidia-smi` |
| OS | Windows 11 Home Single Language 10.0.26200 | `Win32_OperatingSystem` |
| Ollama | 0.40.5 | `GET /api/version`, `ollama --version` |
| Model | `qwen3:4b`, digest prefix `359d7dd4bcda`, 4.0B parameters, Q4_K_M, 2.5 GB | `ollama show`, `GET /api/tags` |
| Model defaults (Modelfile) | temperature 0.6, top_k 20, top_p 0.95 — แต่ Backend ส่ง `temperature=0` ทับ | `ollama show` |
| PowerShell / .NET SDK / Python | 7.4.6 / 10.0.402 / 3.11 | `$PSVersionTable`, `dotnet --version` |

### การตั้งค่า Ollama ที่เกี่ยวกับ Concurrency และ Queue

อ่านจากบรรทัด `server config` ใน log ของ Ollama เอง (`%LOCALAPPDATA%\Ollama\server.log`) ไม่มีตัวแปร `OLLAMA_*` ใน environment ของ User/Machine จึงเป็นค่า default ทั้งหมด:

| ตัวแปร | ค่าจริง | ความหมายต่อ benchmark |
| --- | --- | --- |
| `OLLAMA_NUM_PARALLEL` | 1 | โมเดลประมวลผลทีละคำขอ คำขอที่เหลือรอในคิว |
| `OLLAMA_MAX_QUEUE` | 512 | คิวรับได้ 512 คำขอก่อนตอบ error |
| `OLLAMA_MAX_LOADED_MODELS` | 0 (auto) | ไม่จำกัดตายตัว |
| `OLLAMA_KEEP_ALIVE` | 5m | โมเดลถูก unload เมื่อว่าง 5 นาที → คำขอถัดไปเป็น cold start |
| `OLLAMA_FLASH_ATTENTION` | false | ไม่ได้เปิด flash attention |
| `OLLAMA_CONTEXT_LENGTH` | 0 (auto) | ใช้ค่า auto ของ runtime |
| `OLLAMA_HOST` | `http://127.0.0.1:11434` | loopback เท่านั้น |

### การตั้งค่า Backend ที่เกี่ยวข้อง (`BC-LocalAI/appsettings.json` และ AI-002 contract)

| ค่า | ค่าจริง |
| --- | --- |
| `Ollama:TimeoutSeconds` (HttpClient timeout ไป Ollama) | 180 วินาที → หมดเวลาแล้วตอบ HTTP 504 `AI_TIMEOUT` |
| `num_predict` | 256 → คำตอบที่ยาวเกินจบด้วย `done_reason=length` และ Backend ตอบ HTTP 502 `AI_INVALID_RESPONSE` |
| `think` / `stream` / `temperature` | false / false / 0 |
| `format` | JSON schema `{ "answer": string }` |
| Concurrency limit ใน Backend | ไม่มี (ส่งต่อทุกคำขอไป Ollama ทันที) |

ไม่ได้แก้ค่าเหล่านี้ระหว่างทดสอบ (ข้อ 11 Out of Scope)

## 2. Scenarios

| Test Case | Concurrent Requests | วัตถุประสงค์ |
| --- | ---: | --- |
| TC-01 | 1 | baseline ไม่มีคิว |
| TC-02 | 2 | concurrency ต่ำ |
| TC-03 | 5 | ผลกระทบจากการรอคิว |
| TC-04 | 10 | ข้อจำกัดและเสถียรภาพภายใต้โหลด |

แต่ละ TC รันกับ 3 ความยาวคำตอบ และกับ 2 เป้าหมาย (Ollama Direct, Backend) รวม 24 scenarios

| Profile | คำสั่งใน prompt | ตัวอย่าง |
| --- | --- | --- |
| short | คำเดียว / ตัวเลข | `What is the capital of Japan? Answer with one word.` |
| medium | 3 ประโยค | `Explain in exactly three sentences why the sky looks blue.` |
| long | ย่อหน้าประมาณ 120 คำ | `Write one paragraph of about 120 words explaining how a web browser loads a page.` |

แต่ละ profile มี 5 prompt (อังกฤษ 4 ไทย 1) ใช้วนตามลำดับเพื่อไม่ให้ prompt เดิมซ้ำทุกคำขอ รายการเต็มอยู่ใน `scripts/benchmark/BenchmarkCommon.psm1` และบันทึกซ้ำในไฟล์ summary JSON

**Round** = ยิงคำขอ N ตัวพร้อมกัน (N = concurrency) แล้วรอให้ครบทุกตัวก่อนเริ่มรอบถัดไป แต่ละ scenario ทำ 5 รอบ จำนวนตัวอย่าง latency ต่อ scenario = 5 × N (5, 10, 25, 50)

## 3. Metrics

Issue ไม่มีข้อ 4 (ข้ามจาก 3.2 ไป 5) จึงกำหนดชุด metrics จากข้อ 9 Acceptance Criteria ดังนี้:

| Metric | นิยาม |
| --- | --- |
| Latency | เวลาจากส่งคำขอจนได้ body ครบ ต่อคำขอ วัดฝั่ง client (`elapsed_ms`); รายงาน min / mean / P50 / P95 / P99 / max เฉพาะคำขอที่สำเร็จ, percentile แบบ nearest-rank |
| Sample size | จำนวน latency ที่ใช้คำนวณ; **P99 ถือว่าเชื่อถือได้เมื่อ n ≥ 100 เท่านั้น** (`p99_reliable`) |
| Throughput | คำขอสำเร็จ ÷ เวลารวมของทุกรอบ (req/s); และ output tokens ÷ เวลารวม (tokens/s, เฉพาะ Direct) |
| Success rate | สำเร็จ ÷ ทั้งหมด |
| Timeout rate | (client timeout + server timeout) ÷ ทั้งหมด |
| Generation speed | `eval_count ÷ eval_duration` ต่อคำขอ (tokens/s, เฉพาะ Direct) |
| Queue wait (ประมาณ) | `elapsed_ms − (load + prompt_eval + eval duration)` ของ Ollama = เวลาที่เหลือนอกการประมวลผล ซึ่งคือเวลารอคิว (เฉพาะ Direct) ไม่ใช้ `total_duration` เพราะรวมเวลารอคิวไว้แล้ว |
| Resource | CPU % (ทั้งเครื่อง), RAM ว่าง, working set ของ process `ollama*`, GPU %, VRAM ที่ใช้ — สุ่มทุก 1 วินาที แล้วสรุป avg/max/min ต่อช่วงเวลาของ scenario |

### การจัดประเภทผลลัพธ์ของแต่ละคำขอ

| outcome | เงื่อนไข |
| --- | --- |
| `success` | Direct: HTTP 200, `done=true`, `done_reason=stop`, model ถูก, `response` เป็น JSON `{answer}` ไม่ว่าง; Backend: HTTP 200, `response` ไม่ว่าง, `model=qwen3:4b` |
| `truncated` | Direct: `done_reason=length` (เกิน `num_predict`) |
| `server_timeout` | Backend: HTTP 504 (`AI_TIMEOUT`) |
| `server_error` | HTTP อื่นที่ไม่ใช่ 200 พร้อม `error_code` จาก body (เช่น `AI_INVALID_RESPONSE`) |
| `client_timeout` | เกิน `-TimeoutSec` ฝั่ง script (ค่าเริ่มต้น 300 วินาที มากกว่า Backend 180 วินาที เพื่อแยกจาก server timeout) |
| `connection_error` / `invalid_response` | เชื่อมต่อไม่ได้ / HTTP 200 แต่ body ไม่ตรง contract |

## 4. Methodology

1. ใช้เครื่องและ configuration เดียวกันตลอด ไม่เปลี่ยน model/config ระหว่างรอบ
2. **Cold start แยกจาก warm**: ก่อนแต่ละ target รัน `ollama stop qwen3:4b` และตรวจ `ollama ps` ว่าว่าง แล้วส่ง `-ColdStartNote`; คำขอแรกบันทึกเป็น `phase=cold` และไม่รวมในสถิติ
3. **Warm-up**: 3 คำขอ (`phase=warmup`) ไม่รวมในสถิติ
4. รันตามลำดับ short → medium → long และ concurrency 1 → 2 → 5 → 10, 5 รอบต่อ scenario
5. บันทึก start/end time (UTC) และ offset ของทุกคำขอ พร้อม outcome, HTTP status, error code และค่าจาก Ollama
6. Direct กับ Backend ใช้ prompt ชุดเดียวกัน และ request ไป Ollama ใช้ body เดียวกับ `OllamaAIService` (มี unit test กัน drift กับ `OllamaGenerateRequest.cs`)
7. **Safety stop**: ก่อนทุกรอบตรวจ RAM ว่างล่าสุด ถ้าต่ำกว่า `-MinAvailableMemoryMB` (ค่าเริ่มต้น 1,024 MB) หยุดทันที บันทึกเหตุผลใน `aborted` และคืน exit code 1
8. ปิด Backend อื่นและงานหนักบนเครื่องเท่าที่ทำได้ แต่เครื่องเป็น laptop ใช้งานจริง จึงมีโปรแกรมอื่นเปิดอยู่ (บันทึกเป็นข้อจำกัด)

จำนวนตัวอย่างต่อ scenario ไม่ถึง 100 ผลรอบนี้จึงเป็น **Preliminary Results** ไม่ใช่ข้อสรุปความสามารถรองรับโหลดจริง

## 5. Tools

| เครื่องมือ | หน้าที่ |
| --- | --- |
| `scripts/benchmark/ollama-benchmark.ps1` | ยิง `POST /api/generate` ตรงไป Ollama (loopback) |
| `scripts/benchmark/backend-benchmark.ps1` | ยิง `POST /api/chat` ของ Backend (ปฏิเสธ port 11434) |
| `scripts/benchmark/BenchmarkCommon.psm1` | engine ร่วม: concurrency ด้วย `HttpClient` async, จับเวลา, จัดประเภท, sampler CPU/RAM/GPU (CIM + `nvidia-smi`), สรุปผล |
| `tests/test_benchmark_scripts.py` | unit tests กับ mock server: concurrency จริง, การจัดประเภท, append CSV, safety stop, input validation, contract drift |

script ไม่ใช้ proxy, ไม่ตาม redirect, ไม่เก็บ credential/ชื่อเครื่อง/ชื่อผู้ใช้/path และไม่เปลี่ยน configuration ใด ๆ

## 6. วิธีรันซ้ำ

ต้องมี PowerShell 7, .NET 10, Ollama และ `qwen3:4b` ทำจากราก repository

```powershell
dotnet build .\BC-LocalAI\BC-LocalAI.csproj
dotnet run --no-build --project .\BC-LocalAI\BC-LocalAI.csproj --no-launch-profile --urls http://localhost:5290
```

อีกหน้าต่าง:

```powershell
ollama stop qwen3:4b
ollama ps
pwsh -NoProfile -File .\scripts\benchmark\ollama-benchmark.ps1 -ColdStartNote 'Model unloaded; ollama ps empty before run.'

ollama stop qwen3:4b
ollama ps
pwsh -NoProfile -File .\scripts\benchmark\backend-benchmark.ps1 -AppendCsv -ColdStartNote 'Model unloaded; ollama ps empty before run.'
```

ทดสอบย่อเร็ว ๆ: `-Concurrency 1,2 -Profiles short -Rounds 2` และเพิ่มตัวอย่างสำหรับ P99 ด้วย `-Rounds 10` ที่ concurrency 10 (100 ตัวอย่าง)

## 7. Outputs (D3)

| ไฟล์ | เนื้อหา |
| --- | --- |
| `docs/performance/benchmark-results.csv` | 1 แถวต่อคำขอ ทั้งสอง target (run_id, target, phase, profile, concurrency, round, start/end UTC, elapsed, HTTP, outcome, error_code, token counts, durations, queue wait) |
| `docs/performance/benchmark-summary-ollama.json` / `-backend.json` | parameters, prompts, environment, cold start, สรุปต่อ scenario, resource samples |
| `docs/performance/benchmark-run-*.txt` | console output ของแต่ละรอบ |
