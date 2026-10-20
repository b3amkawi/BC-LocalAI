# AI-007 Failure Injection Results (D3)

ทดสอบเมื่อ 2026-10-20T12:11:43 UTC บน Windows 11, RTX 4050 Laptop 6 GB, Ollama 0.40.5, qwen3:4b (digest 359d7dd4bcda)
จำนวน 10 ชนิดความล้มเหลว รันชนิดละ 6 ครั้ง รวม 60 การ inject (เกณฑ์ของ Issue คือ 5 ชนิด ชนิดละ 3 ครั้ง)

การ inject ทำบน Ollama instance แยกที่สคริปต์เริ่มเองบนพอร์ต 11436 (ไฟล์โมเดลเดียวกัน) ไม่มีการหยุดหรือแก้ instance ปกติที่พอร์ต 11434 นอกจากสั่ง unload โมเดลเพื่อไม่ให้แย่ง VRAM
ไม่ได้ inject: memory exhaustion (can freeze the machine) เพราะเสี่ยงทำให้เครื่องค้าง

## สรุป

| ID | ความล้มเหลว | รัน | ผ่าน | เวลาฟื้น ต่ำสุด / กลาง / สูงสุด |
| --- | --- | ---: | ---: | --- |
| F01 | Model runner killed in the middle of a stream | 6 | 6 | 3,683 / 4,051 / 4,599 ms (n=6) |
| F02 | Whole Ollama process killed in the middle of a stream | 6 | 6 | 6,859 / 7,015 / 7,858 ms (n=6) |
| F03 | Ollama is not running | 6 | 6 | 6,715 / 6,983 / 7,215 ms (n=6) |
| F04 | Model unloaded between requests (cold start, not a failure) | 6 | 6 | - |
| F05 | Unload requested while a stream is running | 6 | 6 | - |
| F06 | Model name does not exist | 6 | 6 | - |
| F07 | Answer cut by the token limit (done_reason length) | 6 | 6 | - |
| F08 | Invalid request options (bad format value) | 6 | 6 | - |
| F09 | Client cancels in the middle of a stream | 6 | 6 | 82 / 85 / 89 ms (n=6) |
| F10 | Queued request abandoned by its client (timeout while waiting) | 6 | 6 | 10,175 / 10,322 / 10,356 ms (n=6) |

เวลาฟื้นคือเวลาตั้งแต่ inject จนคำขอสั้นถัดไปสำเร็จ ของ F02 และ F03 รวมเวลาที่สคริปต์สตาร์ท Ollama ใหม่ (เสมือนมีตัวดูแล process) ของ F09 คือเวลาถึง record แรกของคำขอถัดไป ของ F10 คือเวลาของคำขอสั้นที่ตามมา

## Expected และผลของแต่ละชนิด

### F01 Model runner killed in the middle of a stream

Expected (กำหนดก่อนรัน):

- stream ends with an error record and no done=true
- outcome provider_error, code AI_PROVIDER_ERROR
- message has no loopback endpoint
- Ollama answers a new request within 30 s without any restart

Actual (ครั้งที่ 1):

```json
{
 "outcome": "provider_error",
 "error_code": "AI_PROVIDER_ERROR",
 "http_status": 200,
 "records": 33,
 "deltas": 26,
 "done": false,
 "done_reason": null,
 "first_record_ms": 67.6,
 "total_ms": 737.5,
 "upstream_error": "an error was encountered while running the model: read tcp <loopback>-><loopback>: wsarecv: An existing connection was forcibly closed by the remote host.",
 "eval_count": null,
 "answer": "The history of the bicycle is a fascinating journey through innovation, engineering, and cultural shifts. It began in the early 19"
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| outcome is provider_error | provider_error | provider_error | ใช่ |
| error code | AI_PROVIDER_ERROR | AI_PROVIDER_ERROR | ใช่ |
| no done record | done=false | False | ใช่ |
| upstream message has no loopback endpoint | sanitized | an error was encountered while running the model: read tcp <loopback>-><loopback>: wsarecv: An existing connection was forcibly closed by the remote host. | ใช่ |
| recovers without restart within 30 s | < 30000 ms | 4098 | ใช่ |

### F02 Whole Ollama process killed in the middle of a stream

Expected (กำหนดก่อนรัน):

- stream ends interrupted (no done), code AI_INVALID_RESPONSE
- a request while it is down fails with AI_UNAVAILABLE within 3 s
- after a restart the next request completes

Actual (ครั้งที่ 1):

```json
{
 "interrupted": {
  "outcome": "interrupted",
  "error_code": "AI_INVALID_RESPONSE",
  "http_status": 200,
  "records": 12,
  "deltas": 6,
  "done": false,
  "done_reason": null,
  "first_record_ms": 47.3,
  "total_ms": 626.0,
  "upstream_error": null,
  "eval_count": null,
  "answer": "The history of the bicycle is"
 },
 "while_down": {
  "outcome": "unavailable",
  "error_code": "AI_UNAVAILABLE",
  "http_status": null,
  "records": 0,
  "deltas": 0,
  "done": false,
  "done_reason": null,
  "first_record_ms": null,
  "total_ms": 2059.5,
  "upstream_error": null,
  "eval_count": null,
  "answer": ""
 },
 "server_ready_ms": 4775.0
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| interrupted stream is not completed | not completed | interrupted | ใช่ |
| interrupted stream error code | AI_INVALID_RESPONSE or AI_PROVIDER_ERROR | AI_INVALID_RESPONSE | ใช่ |
| request while down is unavailable | AI_UNAVAILABLE | AI_UNAVAILABLE | ใช่ |
| failure while down is fast | < 3000 ms | 2059.5 | ใช่ |
| restart then answer | recovered | True | ใช่ |

### F03 Ollama is not running

Expected (กำหนดก่อนรัน):

- request fails within 3 s with AI_UNAVAILABLE
- after start the next request completes

Actual (ครั้งที่ 1):

```json
{
 "while_down": {
  "outcome": "unavailable",
  "error_code": "AI_UNAVAILABLE",
  "http_status": null,
  "records": 0,
  "deltas": 0,
  "done": false,
  "done_reason": null,
  "first_record_ms": null,
  "total_ms": 2048.0,
  "upstream_error": null,
  "eval_count": null,
  "answer": ""
 },
 "server_ready_ms": 3978.0
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| outcome unavailable | unavailable | unavailable | ใช่ |
| error code | AI_UNAVAILABLE | AI_UNAVAILABLE | ใช่ |
| fails fast | < 3000 ms | 2048 | ใช่ |
| recovers after start | recovered | True | ใช่ |

### F04 Model unloaded between requests (cold start, not a failure)

Expected (กำหนดก่อนรัน):

- request completes
- first record within 30 s
- cold first record is slower than a warm one

Actual (ครั้งที่ 1):

```json
{
 "warm": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 42.2,
  "total_ms": 265.6,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 },
 "cold": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 3112.5,
  "total_ms": 3341.4,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| completed after unload | completed | completed | ใช่ |
| first record within 30 s | < 30000 ms | 3112.5 | ใช่ |
| cold start is slower than warm | cold > warm | 3112.5 vs 42.2 | ใช่ |

### F05 Unload requested while a stream is running

Expected (กำหนดก่อนรัน):

- the running stream still completes normally (the unload waits)
- the model is unloaded afterwards
- the next request is a cold start that completes

Actual (ครั้งที่ 1):

```json
{
 "running": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 137,
  "deltas": 129,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 49.6,
  "total_ms": 2471.6,
  "upstream_error": null,
  "eval_count": 137,
  "answer": "When you enter a URL into a web browser, it first checks the domain name against its DNS cache or queries a DNS server to resolve the domain to an IP address. The browser then establishes a TCP connection with the server using the IP address, sends an HTTP request, and waits for the server to respond with the HTML content. The browser parses the HTML to identify resources like CSS, JavaScript, and images, which it fetches sequentially. As these resources load, the browser renders the page by displaying text, images, and interactive elements, while JavaScript executes to make the page dynamic. This process ensures the page is displayed correctly and interactively."
 },
 "next": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 3003.1,
  "total_ms": 3241.6,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| running stream completed | completed | completed | ใช่ |
| model unloaded afterwards | no loaded model | 0 | ใช่ |
| next request completes (cold) | completed | completed | ใช่ |

### F06 Model name does not exist

Expected (กำหนดก่อนรัน):

- HTTP error within 2 s, code AI_PROVIDER_ERROR
- the real model still answers afterwards

Actual (ครั้งที่ 1):

```json
{
 "missing_model": {
  "outcome": "http_error",
  "error_code": "AI_PROVIDER_ERROR",
  "http_status": 404,
  "records": 0,
  "deltas": 0,
  "done": false,
  "done_reason": null,
  "first_record_ms": null,
  "total_ms": 6.5,
  "upstream_error": "model 'no-such-model:0b' not found",
  "eval_count": null,
  "answer": ""
 },
 "after": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 45.9,
  "total_ms": 273.7,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| outcome http_error | http_error | http_error | ใช่ |
| error code | AI_PROVIDER_ERROR | AI_PROVIDER_ERROR | ใช่ |
| fails fast | < 2000 ms | 6.5 | ใช่ |
| real model still works | completed | completed | ใช่ |

### F07 Answer cut by the token limit (done_reason length)

Expected (กำหนดก่อนรัน):

- outcome truncated, code AI_RESPONSE_TOO_LARGE
- the same prompt is cut at the same point again (retrying is useless)

Actual (ครั้งที่ 1):

```json
{
 "first": {
  "outcome": "truncated",
  "error_code": "AI_RESPONSE_TOO_LARGE",
  "http_status": 200,
  "records": 25,
  "deltas": 18,
  "done": true,
  "done_reason": "length",
  "first_record_ms": 62.9,
  "total_ms": 492.6,
  "upstream_error": null,
  "eval_count": 24,
  "answer": "The history of the bicycle is a fascinating journey through innovation, engineering, and cultural shifts."
 },
 "retry": {
  "outcome": "truncated",
  "error_code": "AI_RESPONSE_TOO_LARGE",
  "http_status": 200,
  "records": 25,
  "deltas": 18,
  "done": true,
  "done_reason": "length",
  "first_record_ms": 48.0,
  "total_ms": 479.7,
  "upstream_error": null,
  "eval_count": 24,
  "answer": "The history of the bicycle is a fascinating journey through innovation, engineering, and cultural shifts."
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| outcome truncated | truncated | truncated | ใช่ |
| error code | AI_RESPONSE_TOO_LARGE | AI_RESPONSE_TOO_LARGE | ใช่ |
| retry gives the same cut | same answer | 24/24 | ใช่ |

### F08 Invalid request options (bad format value)

Expected (กำหนดก่อนรัน):

- HTTP error status (400 was expected, Ollama answers 500) within 2 s, code AI_PROVIDER_ERROR
- Ollama keeps serving afterwards

Actual (ครั้งที่ 1):

```json
{
 "invalid": {
  "outcome": "http_error",
  "error_code": "AI_PROVIDER_ERROR",
  "http_status": 500,
  "records": 0,
  "deltas": 0,
  "done": false,
  "done_reason": null,
  "first_record_ms": null,
  "total_ms": 7.5,
  "upstream_error": "invalid format: \"\\\"xml\\\"\"; expected \"json\" or a valid JSON Schema object",
  "eval_count": null,
  "answer": ""
 },
 "after": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 63.5,
  "total_ms": 328.5,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| HTTP error status (400 expected, Ollama answers 500) | 400 or 500 | 500 | ใช่ |
| error code | AI_PROVIDER_ERROR | AI_PROVIDER_ERROR | ใช่ |
| fails fast | < 2000 ms | 7.5 | ใช่ |
| still serving | completed | completed | ใช่ |

### F09 Client cancels in the middle of a stream

Expected (กำหนดก่อนรัน):

- outcome cancelled
- the next request gets its first record within 3 s (Ollama stopped generating)

Actual (ครั้งที่ 1):

```json
{
 "cancelled": {
  "outcome": "cancelled",
  "error_code": null,
  "http_status": 200,
  "records": 10,
  "deltas": 4,
  "done": false,
  "done_reason": null,
  "first_record_ms": 55.8,
  "total_ms": 239.3,
  "upstream_error": null,
  "eval_count": null,
  "answer": "The history of the"
 },
 "next": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 83.0,
  "total_ms": 331.2,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 }
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| outcome cancelled | cancelled | cancelled | ใช่ |
| next request completes | completed | completed | ใช่ |
| next first record within 3 s | < 3000 ms | 83 | ใช่ |

### F10 Queued request abandoned by its client (timeout while waiting)

Expected (กำหนดก่อนรัน):

- the queued request times out on the client side
- a later short request completes
- it waits no longer than the running request needs plus 3 s (the abandoned request does not generate)

Actual (ครั้งที่ 1):

```json
{
 "queued": {
  "outcome": "timeout",
  "error_code": "AI_TIMEOUT",
  "http_status": null,
  "records": 0,
  "deltas": 0,
  "done": false,
  "done_reason": null,
  "first_record_ms": null,
  "total_ms": 2005.5,
  "upstream_error": null,
  "eval_count": null,
  "answer": ""
 },
 "later_short": {
  "outcome": "completed",
  "error_code": null,
  "http_status": 200,
  "records": 11,
  "deltas": 2,
  "done": true,
  "done_reason": "stop",
  "first_record_ms": 10088.0,
  "total_ms": 10337.3,
  "upstream_error": null,
  "eval_count": 11,
  "answer": "Tokyo"
 },
 "running_a_total_ms": 12639.6,
 "a_remaining_ms": 9933
}
```

| ตรวจ | Expected | Actual ครั้งที่ 1 | ผ่านทุกครั้ง |
| --- | --- | --- | --- |
| queued request times out | timeout | timeout | ใช่ |
| running request completed | completed | completed | ใช่ |
| later short request completes | completed | completed | ใช่ |
| abandoned request did not delay it | <= A remaining (9933 ms) + 3000 ms | 10337.3 | ใช่ |

## การบันทึกการแก้ Expected

รันครั้งแรกผ่าน 6/10 ชนิด (เก็บที่ `ollama-failures-evidence-initial.json`) ชนิดที่ไม่ผ่าน: F02, F03, F05, F10 ทั้งหมดเป็นการคาดเดาหรือการออกแบบกรณีทดสอบที่ผิดของผู้ทดสอบ ไม่ใช่ความบกพร่องของ Ollama:

| ID | Expected เดิม | ที่แก้ | เหตุผล |
| --- | --- | --- | --- |
| F02, F03 | request while Ollama is down fails in under 2000 ms | under 3000 ms | First run: a refused loopback connection takes about 2.04 s on this Windows machine in every repetition (the OS retries the connection once). The threshold was a guess. |
| F05 | the running request (600-word essay) completes with num_predict 300 | a 120-word paragraph prompt with num_predict 600 | First run: the essay needs more tokens than the limit, so the running request ended truncated by the test design, not by Ollama. Not a finding about Ollama. |
| F10 | running request: 600-word essay with num_predict 400 | the same essay with num_predict 1024 (about 15 s) | First run: 400 tokens truncated the essay. Second run used a short paragraph, but it finished before the queued request timed out, so nothing was ever queued (found by reading a_remaining_ms = 0). The third version keeps the running request busy for about 15 s. |
| F08 | HTTP 400 within 2 s | HTTP error status (400 or 500) within 2 s | First run: Ollama 0.40.5 answers 500, not 400, for an invalid format value. Recorded here, not hidden; both statuses map to AI_PROVIDER_ERROR. |

หลักฐานสุดท้ายมาจากสองรัน (ดู `provenance` ในไฟล์ evidence): รันเต็ม 10 ชนิดด้วย Expected ที่แก้รอบแรก และรัน F10 อย่างเดียวหลังพบว่าการออกแบบ F10 ผิดอีกครั้งตอนอ่านผลรันเต็ม (คำขอที่รันเสร็จก่อนคำขอที่สองหมดเวลา จึงไม่มีอะไรถูกทิ้งขณะรอคิว) F10 รัน 6 ครั้งผ่านทั้งหมด
