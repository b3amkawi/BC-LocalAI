# AI-004: Handoff to Backend Developer (D5)

สรุปสิ่งที่ Backend Developer ควรนำไปใช้ใน BE-003 (Response Length & Timeout) และ BE-004 (Streaming) ข้อมูลทั้งหมดเป็นผล **Preliminary** บนเครื่อง RTX 4050 6 GB (n ≤ 50 ต่อ scenario) อ้างตัวเลขและเงื่อนไขเต็มได้ใน [Performance Report](benchmark-report.md) AI-004 ไม่ได้แก้ Backend, timeout หรือ config ใด ๆ ตามขอบเขต Issue

## 1. สิ่งที่วัดได้และใช้ได้เลย

| ข้อเท็จจริง | หลักฐาน | ใช้ทำอะไร |
| --- | --- | --- |
| คำตอบที่ต้องใช้เกิน 256 token ถูกตัด (`done_reason=length`) แล้ว Backend ตอบ **502 `AI_INVALID_RESPONSE`** ทุกครั้ง (18/18 ที่ prompt ย่อหน้าภาษาไทย ≈ 120 คำ) ส่วนย่อหน้าอังกฤษ ≈ 120 คำใช้ ≈ 137 token จบปกติ | Report ข้อ 6.1 | BE-003: ตัดสินใจเรื่องงบ token และการแสดงผลเมื่อถูกตัด |
| Ollama ประมวลผลทีละคำขอ: throughput คงที่ (short ≈ 3.4, medium ≈ 0.67, long ≈ 0.30 req/s) ไม่ว่า concurrency 1 หรือ 10 มีแต่ latency ที่เพิ่ม (medium P50 1.3 s → 7.5 s ที่ 10 ตัวพร้อมกัน) | Report ข้อ 6.2 | BE-003: การปล่อยคำขอพร้อมกันเข้า Ollama ไม่ช่วยอะไร |
| Backend ไม่เพิ่ม overhead ที่วัดได้ (ผลต่าง P50 ที่ concurrency 1–2 อยู่ใน −156…+4 ms; throughput ต่างกัน ≤ 6%) | Report ข้อ 5 | ไม่ต้องเสียเวลา optimize ชั้น Backend ก่อน |
| ตลอดการทดสอบไม่เคยเกิด 504 `AI_TIMEOUT` (คำขอที่นานที่สุด ≈ 27 s จาก timeout 180 s) | Report ข้อ 4 | ค่า 180 s ไม่ได้ถูกแตะที่โหลดระดับนี้ |
| cold start ≈ 4–5 s (โหลดโมเดลเข้า VRAM ≈ 3.9 s) หลังโมเดลถูก unload; `OLLAMA_KEEP_ALIVE` default = 5 นาที | Report ข้อ 2 (n=1) | UI ควรรับมือคำขอแรกที่ช้ากว่าปกติ |
| VRAM ใช้ 3.2 GB จาก 6.1 GB คงที่ และ GPU ≈ 90% เมื่อคำตอบยาว | Report ข้อ 5 | GPU คือคอขวด ไม่ใช่ CPU/RAM/VRAM |

## 2. ข้อเสนอสำหรับ BE-003 (Timeout / Concurrency Policy)

ข้อเสนอเหล่านี้มาจากหลักฐานข้างต้น ต้องให้ Tech Lead อนุมัติก่อนเปลี่ยน AI-002 contract

1. **คำตอบที่ถูกตัด:** เลือกอย่างใดอย่างหนึ่ง (a) เพิ่ม `num_predict` ใน contract, หรือ (b) ยอมรับ `done_reason=length` เป็นคำตอบบางส่วนพร้อมธง `truncated` ให้ UI แจ้งผู้ใช้ แทนการตอบ 502 ซึ่งให้ผู้ใช้เห็นแค่ข้อผิดพลาดทั้งที่โมเดลทำงานปกติ ข้อ (a) ต้องวัดซ้ำ: คำตอบยาวขึ้นแปลว่าคิวนานขึ้น (generate ≈ 60 tokens/s)
2. **นโยบาย concurrency ที่ Backend:** จากข้อมูล การส่งเข้า Ollama ครั้งละ 1 คำขอไม่ทำให้ throughput เสียไป (short 3.42 req/s ที่ 1 ตัว เทียบ 3.47 ที่ 10 ตัว) แต่ช่วยให้ Backend ควบคุมคิวเอง ข้อเสนอคือใช้ semaphore จำกัดจำนวนคำขอที่ส่งเข้า Ollama พร้อมกัน และคิวที่รออยู่มีขนาดจำกัด ถ้าคิวเต็มให้ตอบ 429/503 พร้อมข้อความที่ UI แสดงได้ทันที แทนปล่อยให้ผู้ใช้รอจนครบ 180 s
3. **เพดานเวลารอ:** ถ้า UX ต้องการให้รอไม่เกิน 30 วินาที ที่ throughput medium 0.67 req/s คิวควรยาวไม่เกินราว 20 คำขอ (**ประมาณการ** จาก throughput ไม่ได้ทดสอบ) ให้ตั้งขนาดคิวจาก throughput ที่วัดซ้ำบนเครื่องจริง ไม่ใช่จากตัวเลขนี้ตรง ๆ
4. **ยกเลิกคำขอเมื่อผู้ใช้ปิดหน้า:** ส่ง `CancellationToken` ไปถึง `HttpClient` ที่เรียก Ollama (ตอนนี้ `OllamaAIService` รับ token อยู่แล้ว) เพื่อไม่ให้คำขอที่ไม่มีคนรอแล้วกินคิว **AI-004 ไม่ได้ทดสอบ** ว่า Ollama หยุด generate จริงเมื่อ client ตัดการเชื่อมต่อ ต้องตรวจใน BE-003
5. **timeout 180 s:** หลักฐานรอบนี้ไม่ได้ทำให้ต้องเปลี่ยน (ไม่เคยเกิน 27 s) แต่ค่านี้ควรเป็นเพดานของ "คำขอเดียว" ส่วนการรอคิวควรมี timeout แยก (ข้อ 2) มิฉะนั้นเวลารอคิวกับเวลา generate ปนกันและแยกสาเหตุไม่ได้ (Report ข้อ 6.4 ประมาณการว่าคิว 54 คำขอ long ก็เต็ม 180 s แล้ว)

## 3. ข้อควรคำนึงสำหรับ BE-004 / AI-005 (Streaming)

- **สิ่งที่ streaming ช่วยได้:** ผู้ใช้เห็น token แรกเร็วขึ้น และเห็นความคืบหน้าของคำตอบยาว (ปัจจุบัน long c=1 รอ ≈ 2.3 s และ c=10 รอ P50 ≈ 11–12 s จนกว่าจะเห็นอะไรเลย)
- **สิ่งที่ streaming ไม่ช่วย:** throughput รวมและเวลารอคิวก่อนเริ่ม generate ไม่เปลี่ยน (Report ข้อ 6.2) ผู้ใช้ที่เป็นคนที่ 10 ในคิวยังรอก่อนเห็น token แรก จึงยังต้องมีนโยบายคิวตามข้อ 2
- **ยังไม่มีข้อมูล time-to-first-token (TTFT)** เพราะ AI-004 ทดสอบแบบ non-streaming ตาม contract ปัจจุบัน ควรเพิ่มการวัด TTFT และอัตรา token/วินาทีใน AI-005 โดยใช้ `ollama-benchmark.ps1` เป็นฐาน
- โครงสร้าง error ระหว่าง stream (เช่น ถูกตัดกลางทางที่ 256 token) ต้องกำหนดใน contract ของ AI-005 เพราะ 502 หลังส่ง token ไปแล้วทำไม่ได้ (ข้อเสนอข้อ 1 ของ BE-003 เกี่ยวข้องโดยตรง)
- การตั้ง `Ollama:TimeoutSeconds` กับ stream ต้องคิดเป็น "เวลาระหว่าง token" ไม่ใช่เวลารวมทั้งคำขอ

## 4. สิ่งที่ยังไม่รู้ (อย่านำไปตัดสินใจว่าเป็นข้อเท็จจริง)

- ความสามารถรองรับจริงเมื่อ concurrency เกิน 10 หรือการมาถึงแบบสุ่ม (ทดสอบแต่ burst ≤ 10)
- พฤติกรรมเมื่อ 504 เกิดจริงที่โหลดสูง และ Ollama ปฏิเสธคำขอเมื่อคิวเกิน `OLLAMA_MAX_QUEUE=512` อย่างไร
- ผลของ `OLLAMA_NUM_PARALLEL` > 1 (ยังเหลือ VRAM ≈ 3 GB แต่ไม่ได้ทดสอบ)
- ตัวเลขบนเครื่องเป้าหมาย RTX 4060/4070 และ P99 ที่เชื่อถือได้ (ต้องมีตัวอย่าง ≥ 100 ต่อ scenario)

## 5. วิธีรันซ้ำหลังแก้ BE-003/BE-004

ใช้สคริปต์ชุดเดียวกันเพื่อเปรียบเทียบกับ baseline นี้ (ดูขั้นตอนเต็มใน [benchmark-plan.md](benchmark-plan.md) ข้อ 6)

```powershell
pwsh -NoProfile -File .\scripts\benchmark\backend-benchmark.ps1 -ColdStartNote 'Model unloaded; ollama ps empty before run.' -OutputCsv .\docs\performance\benchmark-results-after.csv -OutputJson .\docs\performance\benchmark-summary-backend-after.json
```

ตั้งชื่อไฟล์ผลใหม่เพื่อไม่ทับ baseline และเก็บ `-TimeoutSec` ให้มากกว่า `Ollama:TimeoutSeconds` ของ Backend เสมอ เพื่อแยก client timeout ออกจาก 504
