# AI-006: plan, tools and status

**สถานะ (20 ต.ค. 2026): เครื่องมือและเทสต์เสร็จ แต่ยังไม่มีผล benchmark เพราะรันแล้วพบว่า GPU ถูกโปรแกรมอื่นแย่งใช้** ผลที่วัดตอนนั้นใช้เทียบกับ baseline ของ AI-004 ไม่ได้ จึงหยุดรันและไม่นำมาใช้

## ที่พบตอนรัน
- รันรอบหลัก (NUM_PARALLEL=1, Direct และ Backend สลับกัน) ได้ประมาณ 14 นาที ผ่านไปเพียง 2 จาก 12 scenario (short c=1 เสร็จ short c=2 ยังไม่เสร็จ ซึ่งปกติใช้ราว 1 นาที)
- บันทึกการใช้ GPU ทุก 15 วินาทีด้วย `scripts/benchmark/log-gpu-contention.ps1` พบว่าโปรแกรม `deadlock` (เกม) ใช้ 3D engine เฉลี่ย 50.9% สูงสุด 91.2% (57 ตัวอย่าง) และ VRAM ≈ 2.0 ถึง 2.3 GB ขณะที่ `llama-server` (Ollama) ใช้ 3.0 GB จาก VRAM 6.1 GB ที่มี เหลือไม่พอสำหรับทดสอบ `OLLAMA_NUM_PARALLEL=2`
- Roblox และ AutoPerfect ที่ผู้ใช้อนุญาตให้ปิดไม่ใช่ตัวแย่ง GPU จึงไม่ได้ปิดอะไร และไม่ปิดโปรแกรมที่ไม่ได้รับอนุญาต
- ข้อมูลบางส่วนที่ได้ถูกเก็บไว้นอก repository และไม่ได้นำมารวมในผลใด ๆ

## สิ่งที่ต้องมีก่อนรันใหม่
GPU ว่างจากโปรแกรมอื่น (ปิดเกมหรือโปรแกรมที่ใช้ GPU) และ RAM ว่างอย่างน้อย ≈ 2 GB ตรวจด้วย `nvidia-smi` ว่า VRAM ที่ใช้อยู่ก่อนเริ่ม ≤ ≈ 1.3 GB (ค่าที่เครื่องเดสก์ท็อปใช้เองตอนว่างในการวัด AI-004)

## เครื่องมือที่พร้อมใช้
| สคริปต์ | ทำอะไร |
| --- | --- |
| `scripts/benchmark/run-paired-benchmark.ps1` | รัน benchmark ของ AI-004 โดยให้ Direct และ Backend **สลับกัน** ภายใน scenario เดียวกัน (ตัด drift ระหว่างรอบ) เริ่มต่อจากที่ค้างได้ ใช้ `-SkipBackend` สำหรับการเทียบ NUM_PARALLEL |
| `scripts/benchmark/arrival-and-cancel-benchmark.ps1` | โหมด `Arrival`: คำขอเข้ามาแบบสุ่มตามอัตราที่กำหนด (Poisson) ไม่ใช่ burst โหมด `Cancel`: ยกเลิกคำขอที่รอคิวอยู่แล้ววัดว่าคำขอที่เหลือเสร็จเร็วขึ้นหรือไม่ |
| `scripts/benchmark/summarize-paired.py` | สรุปผลเทียบ baseline AI-004: P50/P95/P99, throughput, ส่วนต่างของ Backend จาก slice ที่สลับกัน (ช่วงเชื่อมั่น 95%), prompt ภาษาไทยที่เคยถูกตัด, ทรัพยากร, NUM_PARALLEL |
| `scripts/benchmark/log-gpu-contention.ps1` | บันทึกว่าโปรแกรมใดใช้ GPU ระหว่างรัน เพื่อบอกว่าผลถูกแย่งหรือไม่ |

เทสต์ของเครื่องมือเหล่านี้ (ไม่ต้องใช้ Ollama): `py -3 -m unittest tests.test_paired_benchmark -v` (15 เทสต์ รวมการรันสคริปต์จริงกับ Backend จำลอง)

## ขนาดการทดสอบที่ตั้งไว้ (1.5 เท่าของ Issue)
| รายการ | Issue กำหนด | ตั้งไว้ |
| --- | --- | --- |
| ตัวอย่างต่อ scenario (24 scenarios) | ≥ 125 | ≥ 188 |
| cold start ต่อเป้าหมาย | 5 | 8 |
| NUM_PARALLEL 1 เทียบ 2 | scenario เดียวกัน | 12 scenario (Direct) × 188 ตัวอย่างต่อค่า |

## คำสั่ง (เมื่อ GPU ว่าง)
```powershell
# Backend ต้องรันอยู่ที่ localhost:5290 (Production) และ Ollama ที่ 11434
pwsh -NoProfile -File scripts\benchmark\log-gpu-contention.ps1 -OutputPath docs\performance\post-be003\gpu-contention.jsonl   # อีกหน้าต่าง
pwsh -NoProfile -File scripts\benchmark\run-paired-benchmark.ps1 -OutputDir docs\performance\post-be003 -MinSamples 188 -SliceRequests 40
pwsh -NoProfile -File scripts\benchmark\cold-start-benchmark.ps1 -Target ollama -Repeats 8 -OutputCsv docs\performance\post-be003\cold-start.csv
pwsh -NoProfile -File scripts\benchmark\cold-start-benchmark.ps1 -Target backend -Repeats 8 -OutputCsv docs\performance\post-be003\cold-start.csv -AppendCsv
python scripts/benchmark/summarize-paired.py --post docs/performance/post-be003 --baseline-csv docs/performance/benchmark-results.csv --baseline-summaries docs/performance/benchmark-summary-ollama.json docs/performance/benchmark-summary-backend.json --output docs/performance/post-be003/summary.json
```
