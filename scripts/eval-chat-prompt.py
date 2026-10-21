#!/usr/bin/env python3
"""AI-008 follow-up: does the chat system prompt make qwen3:4b answer short Thai requests (loopback Ollama only)?

Compares system prompt variants S1 (anti-echo rule), S2 (rule + three examples, the one in OllamaGenerateRequest.SystemPrompt) and S3 on
20 short requests ("ขอโค้ด ...", "ขอสูตร ...") with no history, 2 and 3 earlier turns, using the BE-007 history template with LF line breaks
(the OS newline CRLF changed answers). A "bad" answer repeats the request, is shorter than 10 characters or is only a number.
    python scripts/eval-chat-prompt.py
"""
import json, sys, urllib.request
B = "http://127.0.0.1:11434"
SCHEMA = {"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"], "additionalProperties": False}
BASE = "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer. "
S1 = BASE + ("Always answer the request itself: never repeat or restate the user's message instead of answering it. "
             "A short request such as 'ขอโค้ด ...' or 'ขอสูตร ...' means: give that code or recipe.")
EX = (" Examples of the expected behaviour: "
      "user: 'ขอสูตรไข่เจียว' -> {\"answer\": \"ไข่เจียว: ตีไข่ 2 ฟอง ใส่น้ำปลา 1 ช้อนชา ทอดน้ำมันร้อนจนสุกเหลือง\"}; "
      "user: 'ขอโค้ด python พิมพ์เลข 1 ถึง 3' -> {\"answer\": \"for i in range(1, 4): print(i)\"}; "
      "user: 'ขอวิธีล้างแก้ว' -> {\"answer\": \"ใช้น้ำยาล้างจานกับฟองน้ำ ถูให้ทั่ว แล้วล้างด้วยน้ำสะอาด\"}.")
S2 = S1 + EX
S3 = BASE + ("Always answer the request itself: never repeat or restate the user's message instead of answering it. "
             "The text after 'Current question:' (or the whole message when there is no history) is what you must answer. "
             "A short request such as 'ขอโค้ด ...' or 'ขอสูตร ...' means: give that code or recipe.") + EX
H = [("สวัสดี ช่วยตอบเป็นภาษาไทยสั้น ๆ ว่า 2 บวก 3 เท่ากับเท่าไร", "5"),
     ("เอาผลลัพธ์เมื่อกี้บวก 4 ได้เท่าไร ตอบสั้น ๆ", "9"),
     ("ขอโค้ดเปิดปิดไฟ arduino", "ใช้ digitalWrite สำหรับเปิดปิดไฟ เช่น digitalWrite(ledPin, HIGH) เปิด digitalWrite(ledPin, LOW) ปิด")]
HEADER = "Earlier conversation (oldest first). Use it only as context for the current question:"


def t1(h, q):
    if not h:
        return q
    L = [HEADER, "<conversation>"]
    for a, b in h:
        L += [f"User: {a}", f"Assistant: {b}"]
    return "\n".join(L + ["</conversation>", "Current question:", q])


def gen(system, p):
    body = {"model": "qwen3:4b", "prompt": p, "stream": False, "think": False, "system": system, "format": SCHEMA, "options": {"temperature": 0, "num_predict": 200}}
    r = json.load(urllib.request.urlopen(urllib.request.Request(B + "/api/generate", data=json.dumps(body).encode(), headers={"Content-Type": "application/json"}), timeout=300))
    try:
        return json.loads(r["response"])["answer"]
    except Exception:
        return r["response"]


T20 = ["ขอโค้ดเปิดปิดไฟ arduino", "ขอสูตรต้มยำกุ้ง", "ขอตัวอย่างอีเมลลาป่วย", "ขอโค้ด python เรียงลำดับตัวเลข", "ขอวิธีล้างแอร์", "ขอรายการอุปกรณ์ตั้งแคมป์",
       "ขอโค้ด html ปุ่มกด", "ขอตารางสูตรคูณแม่ 2", "ขอคำแนะนำออกกำลังกายเช้า", "ขอสูตรผัดกะเพรา", "ขอโค้ด sql สร้างตารางผู้ใช้", "ขอตัวอย่างจดหมายสมัครงาน",
       "ขอวิธีต่อ led กับ esp32", "ขอโค้ด javascript หาค่ามากสุด", "ขอวิธีทำความสะอาดแล็ปท็อป", "ขอไอเดียของขวัญวันเกิด", "ขอโค้ด c เขียนวนลูป 1 ถึง 10",
       "ขอสรุปข้อดีของพลังงานแสงอาทิตย์", "ขอรายการเตรียมตัวสอบ", "ขอวิธีตั้งค่า wifi บนเราเตอร์"]


def bad(q, a):
    a = a.strip()
    q = q.strip()
    return a == q or (a.startswith(q) and len(a) < len(q) + 15) or len(a) < 10 or a.replace("+", "").replace("=", "").replace(" ", "").isdigit()


for name, system in (("S1 anti-echo rule", S1), ("S2 +examples", S2), ("S3 +examples+focus", S3)):
    row = []
    for hist in ([], H[:2], H):
        row.append(sum(bad(q, gen(system, t1(hist, q))) for q in T20))
    print(f"{name:22} bad of 20: no history {row[0]}, 2 turns {row[1]}, 3 turns {row[2]}", flush=True)
