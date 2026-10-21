#!/usr/bin/env python3
"""BE-007 AI review: real-model check of the history context (loopback Ollama only).

1. Overflow: a history of digits sized by the OLD estimator (1 token / 3 bytes, budget 1500) vs the NEW one, and what Ollama really reads
   (prompt_eval_count; the context is 4096, a longer prompt is cut silently).
2. Recall: a fact stated in an earlier turn (Thai and English) must be found when the question comes after the history.
    python scripts/check-be007-context.py --output docs/ai/be007-context-check.json
"""
import argparse, json, sys, time, urllib.request

MODEL = "qwen3:4b"
SYSTEM = "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer."
SCHEMA = {"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"], "additionalProperties": False}
HEADER = "Earlier conversation (oldest first). Use it only as context for the current question:"


def call(base, prompt, num_predict=60):
    body = {"model": MODEL, "prompt": prompt, "stream": False, "think": False, "system": SYSTEM, "format": SCHEMA,
            "options": {"temperature": 0, "num_predict": num_predict}}
    req = urllib.request.Request(base + "/api/generate", data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)


def compose(pairs, question):
    lines = [HEADER, "<conversation>"]
    for q, a in pairs:
        lines += [f"User: {q}", f"Assistant: {a}"]
    return "\n".join(lines + ["</conversation>", "Current question:", question])


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--base-url", default="http://127.0.0.1:11434"); ap.add_argument("--output", required=True)
    a = ap.parse_args()
    if not a.base_url.startswith(("http://127.0.0.1", "http://localhost")):
        print("loopback only", file=sys.stderr); return 2
    out = {"generated_utc": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()), "overflow": [], "recall": []}
    # 1. overflow: history of 3 turns whose estimated size is the 1500 budget
    secret = "รหัสลับคือ 7391"
    for name, digits_bytes in (("old estimator (1500 est = 4500 bytes of digits)", 4500), ("new estimator (1500 est = 1500 bytes of digits)", 1500)):
        digits = ("0123456789" * 500)[:digits_bytes]
        prompt = compose([("จำรหัสนี้: 7391", "รับทราบ รหัสคือ 7391"), ("ตัวเลขชุดนี้คืออะไร " + digits, "เป็นชุดตัวเลข")], "รหัสลับที่ฉันบอกไว้ตอนแรกคืออะไร")
        r = call(a.base_url, prompt, 40)
        ans = json.loads(r["response"]).get("answer", "") if r["response"].strip().startswith("{") else r["response"]
        out["overflow"].append({"case": name, "prompt_eval_count": r["prompt_eval_count"], "context": 4096, "answer": ans, "recalled_7391": "7391" in ans})
        print(out["overflow"][-1])
    # 2. recall at normal sizes
    cases = [
        ("thai", [("ฉันชื่อสมชาย และฉันเลี้ยงแมวชื่อมะลิ", "ยินดีที่ได้รู้จักครับ"), ("วันนี้อากาศดี", "ใช่ครับ")], "แมวของฉันชื่ออะไร", "มะลิ"),
        ("english", [("My dog is called Rex and I live in Chiang Mai.", "Nice to meet you."), ("It is sunny today.", "Great.")], "Where do I live?", "Chiang Mai"),
        ("mixed", [("โปรเจกต์ของเราใช้พอร์ต 5290 สำหรับ API", "รับทราบ"), ("ใช้ Ollama กับ qwen3:4b", "โอเค")], "API ของเราใช้พอร์ตอะไร", "5290"),
    ]
    for name, pairs, q, expect in cases:
        hits = 0
        for _ in range(3):
            r = call(a.base_url, compose(pairs, q), 80)
            try: ans = json.loads(r["response"]).get("answer", "")
            except Exception: ans = r["response"]
            hits += expect in ans
        out["recall"].append({"case": name, "expected": expect, "hits_of_3": hits, "last_answer": ans})
        print(out["recall"][-1])
    open(a.output, "w", encoding="utf-8").write(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
