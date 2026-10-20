# AI-004: Handoff to Backend Developer (D5)

สรุปสิ่งที่ Backend Developer ควรนำไปใช้ใน BE-003 (Response Length & Timeout) และ BE-004 (Streaming) ตัวเลขเป็น baseline จากเครื่อง RTX 4050 6 GB (ทุก scenario มีตัวอย่าง 125–150 คำขอ) ดูตัวเลขและเงื่อนไขเต็มใน [Performance Report](benchmark-report.md) AI-004 ไม่ได้แก้ Backend, timeout หรือ config ใด ๆ ตามขอบเขต Issue และ **ผลนี้เป็นผลก่อน BE-003**: Backend ที่ทดสอบยังใช้ `num_predict=256` ตายตัว

## 1. สิ่งที่วัดได้และใช้ได้เลย

| ข้อเท็จจริง | หลักฐาน | ใช้ทำอะไร |
| --- | --- | --- |
| คำตอบที่ต้องใช้เกิน 256 token ถูกตัด (`done_reason=length`) แล้ว Backend ตอบ **502 `AI_INVALID_RESPONSE`** ทุกครั้ง: prompt ภาษาไทยย่อหน้า ≈ 120 คำ 3 ข้อ ถูกตัด **105 จาก 105 ครั้ง** ส่วน prompt ภาษาอังกฤษแบบเดียวกัน 12 ข้อใช้ 119–196 token (มัธยฐาน 136) จบปกติทุกครั้ง | Report ข้อ 8.2 | BE-003: ตัดสินใจเรื่องงบ token และการแสดงผลเมื่อถูกตัด |
| Ollama ประมวลผลทีละคำขอ: throughput คงที่ (short ≈ 4.2–4.5, medium ≈ 0.67–0.74, long ≈ 0.29 req/s) ไม่ว่า concurrency 1 หรือ 10 มีแต่ latency ที่เพิ่ม (medium P50 1.3 s → 7.8 s, P99 16.0 s ที่ 10 ตัวพร้อมกัน; long P50 2.3 s → 14.2 s, P99 28.5 s) | Report ข้อ 4, 8.1 | BE-003: การปล่อยคำขอพร้อมกันเข้า Ollama ไม่ช่วยอะไร |
| Backend มี overhead เล็ก: P50 ต่างจาก Direct ≤ ~6% ที่ medium/long (ยกเว้น long c=2 ซึ่งเป็นผลของการกระจายสองกลุ่ม) และสูงกว่า 11–20% (+26…+137 ms) ที่ short c ≤ 5 **แต่ Direct เองแปรผันระหว่างรอบถึง 25%** จึงแยกไม่ได้ว่าเป็น overhead จริงหรือความแปรผันของรอบ | Report ข้อ 6 | ไม่จำเป็นต้อง optimize ชั้น Backend ก่อน |
| ตลอดการทดสอบ 3,156 คำขอ ไม่เคยเกิด timeout เลย (คำขอที่นานที่สุด 28.7 s จากเพดาน 180 s) | Report ข้อ 4–5 | ค่า 180 s ไม่ได้ถูกแตะที่โหลดระดับนี้ |
| cold start ≈ 4.4–4.7 s (มัธยฐาน 4.66 s Direct / 4.42 s Backend, n=5) โดยโหลดโมเดลเข้า VRAM ≈ 4.2–4.8 s หลังโมเดลถูก unload; `OLLAMA_KEEP_ALIVE` default = 5 นาที | Report ข้อ 3 | UI ควรรับมือคำขอแรกที่ช้ากว่าปกติ |
| VRAM ใช้ 3.2 GB จาก 6.1 GB คงที่ และ GPU ≈ 90% เมื่อคำตอบยาว; process ของ Ollama (server + runner) ใช้ RAM 0.85–1.7 GB | Report ข้อ 7 | GPU คือคอขวด ไม่ใช่ CPU/VRAM |

## 2. ข้อเสนอสำหรับ BE-003 (Timeout / Concurrency Policy)

ข้อเสนอเหล่านี้มาจากหลักฐานข้างต้น ต้องให้ Tech Lead อนุมัติก่อนเปลี่ยน AI-002 contract

1. **คำตอบที่ถูกตัด:** PR #19 ของ BE-003 พบสาเหตุเดียวกันและเปลี่ยนเป็น `Ollama:MaxOutputTokens=1024` กับ `502 AI_RESPONSE_TOO_LARGE` แล้ว ข้อมูลของ AI-004 ที่เพิ่มคือ (a) เกิดทุกครั้งกับคำถามภาษาไทยยาว 105/105 ไม่ใช่สุ่ม (b) การเพิ่มงบ token ทำให้คำตอบยาวขึ้นได้ถึง 4 เท่า ที่ generate ≈ 60 tokens/s หมายถึง 1,024 token ≈ 17 วินาทีต่อคำขอ และ throughput ของ long จะลดลงตามไปด้วย ควรรัน benchmark ซ้ำหลัง merge (ดูข้อ 5)
2. **นโยบาย concurrency ที่ Backend:** จากข้อมูล การส่งเข้า Ollama ครั้งละ 1 คำขอไม่ทำให้ throughput เสียไป (short 4.23 req/s ที่ 1 ตัว เทียบ 4.49 ที่ 10 ตัว) แต่ช่วยให้ Backend ควบคุมคิวเอง ข้อเสนอคือใช้ semaphore จำกัดจำนวนคำขอที่ส่งเข้า Ollama พร้อมกัน และคิวที่รออยู่มีขนาดจำกัด ถ้าคิวเต็มให้ตอบ 429/503 พร้อมข้อความที่ UI แสดงได้ทันที แทนปล่อยให้ผู้ใช้รอจนครบ 180 s
3. **เพดานเวลารอ:** ถ้า UX ต้องการให้รอไม่เกิน 30 วินาที ที่ throughput medium ≈ 0.69 req/s คิวควรยาวไม่เกินราว 20 คำขอ (**ประมาณการ** จาก throughput ไม่ได้ทดสอบ) ให้ตั้งขนาดคิวจาก throughput ที่วัดซ้ำบนเครื่องจริง ไม่ใช่จากตัวเลขนี้ตรง ๆ
4. **ยกเลิกคำขอเมื่อผู้ใช้ปิดหน้า:** ส่ง `CancellationToken` ไปถึง `HttpClient` ที่เรียก Ollama (ตอนนี้ `ChatController` และ `IAIService` รับ token อยู่แล้ว) เพื่อไม่ให้คำขอที่ไม่มีคนรอแล้วกินคิว **AI-004 ไม่ได้ทดสอบ** ว่า Ollama หยุด generate จริงเมื่อ client ตัดการเชื่อมต่อ ต้องตรวจใน BE-003 (หรือในการทดสอบ cancellation ของ AI-005)
5. **timeout 180 s:** หลักฐานรอบนี้ไม่ได้ทำให้ต้องเปลี่ยน (ไม่เคยเกิน 28.7 s) แต่ค่านี้ควรเป็นเพดานของ "คำขอเดียว" ส่วนการรอคิวควรมี timeout แยก (ข้อ 2) มิฉะนั้นเวลารอคิวกับเวลา generate ปนกันและแยกสาเหตุไม่ได้ (Report ข้อ 8.4 ประมาณการว่าคิว ≈ 52 คำขอ long ก็เต็ม 180 s แล้ว)

## 3. ข้อควรคำนึงสำหรับ BE-004 / AI-005 (Streaming)

- **สิ่งที่ streaming ช่วยได้:** ผู้ใช้เห็น token แรกเร็วขึ้น และเห็นความคืบหน้าของคำตอบยาว (ปัจจุบัน long c=1 รอ ≈ 2.3 s และที่ c=10 รอ P50 ≈ 14 s จนกว่าจะเห็นอะไรเลย)
- **สิ่งที่ streaming ไม่ช่วย:** throughput รวมและเวลารอคิวก่อนเริ่ม generate ไม่เปลี่ยน (Report ข้อ 8.1) ผู้ใช้ที่เป็นคนที่ 10 ในคิวยังรอก่อนเห็น token แรก จึงยังต้องมีนโยบายคิวตามข้อ 2
- **ยังไม่มีข้อมูล time-to-first-token (TTFT)** เพราะ AI-004 ทดสอบแบบ non-streaming ตาม contract ปัจจุบัน ควรเพิ่มการวัด TTFT และอัตรา token/วินาทีใน AI-005 โดยใช้ `ollama-benchmark.ps1` เป็นฐาน
- โครงสร้าง error ระหว่าง stream (เช่น ถูกตัดกลางทางที่ถึงเพดาน token) ต้องกำหนดใน contract ของ AI-005 เพราะ 502 หลังส่ง token ไปแล้วทำไม่ได้ (ข้อเสนอข้อ 1 ของ BE-003 เกี่ยวข้องโดยตรง)
- การตั้ง `Ollama:TimeoutSeconds` กับ stream ต้องคิดเป็น "เวลาระหว่าง token" ไม่ใช่เวลารวมทั้งคำขอ
- รูปแบบคำตอบ `format` JSON schema ที่ AI-002 ใช้ไม่เหมาะกับ stream (chunk เป็นเศษ JSON) ประเด็นนี้ AI-005 ต้องตัดสินใจ

## 4. สิ่งที่ยังไม่รู้ (อย่านำไปตัดสินใจว่าเป็นข้อเท็จจริง)

- ความสามารถรองรับจริงเมื่อ concurrency เกิน 10 หรือการมาถึงแบบสุ่ม (ทดสอบแต่ burst ≤ 10)
- พฤติกรรมเมื่อ 504 เกิดจริงที่โหลดสูง และ Ollama ปฏิเสธคำขอเมื่อคิวเกิน `OLLAMA_MAX_QUEUE=512` อย่างไร
- ผลของ `OLLAMA_NUM_PARALLEL` > 1 (ยังเหลือ VRAM ≈ 3 GB แต่ไม่ได้ทดสอบ และอาจทำให้แต่ละคำขอช้าลง)
- ตัวเลขบนเครื่องเป้าหมาย RTX 4060/4070
- Overhead ที่แท้จริงของ Backend ที่ short (ต้องรันสลับ Direct/Backend ในรอบเดียวกัน)

## 5. วิธีรันซ้ำหลังแก้ BE-003/BE-004

ใช้สคริปต์ชุดเดียวกันเพื่อเปรียบเทียบกับ baseline นี้ (ดูขั้นตอนเต็มใน [benchmark-plan.md](benchmark-plan.md) ข้อ 6)

```powershell
ollama stop qwen3:4b
pwsh -NoProfile -File .\scripts\benchmark\backend-benchmark.ps1 -Rounds 15 -MinSamples 125 -ColdStartNote 'Model unloaded; ollama ps empty before run.' -OutputCsv .\docs\performance\benchmark-results-after.csv -OutputJson .\docs\performance\benchmark-summary-backend-after.json
```

ตั้งชื่อไฟล์ผลใหม่เพื่อไม่ทับ baseline และเก็บ `-TimeoutSec` ให้มากกว่า `Ollama:TimeoutSeconds` ของ Backend เสมอ เพื่อแยก client timeout ออกจาก 504 หลังเปลี่ยน `MaxOutputTokens` เป็น 1024 ให้คาดว่า long จะใช้เวลานานขึ้นและ success rate ของ long ควรสูงกว่า 80%
