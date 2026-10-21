#!/usr/bin/env python3
"""QA-001 live checks: the real Backend streaming a real qwen3:4b from a real Ollama.

Run on a machine with Ollama and qwen3:4b (loopback only). CI does not run this file.
Answers of a real model vary, so the checks are structural: a single terminal event, no
model internals or JSON scaffolding, language of the answer, nothing from other requests,
and timings of cancellation and the request after it. Every case runs REPEATS times.

    python tests/e2e/live_streaming_e2e.py --output docs/qa/qa-001-live-evidence.json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import streaming_e2e as h  # noqa: E402

REPEATS = 2
THAI = re.compile(r"[฀-๿]")
CANARY = "CANARY-LIVE-5e1a77"
OLLAMA = "http://127.0.0.1:11434"


def ollama(*args, timeout=30):
    return subprocess.run(["ollama", *args], capture_output=True, text=True, timeout=timeout)


def loaded_models():
    import urllib.request
    with urllib.request.urlopen(OLLAMA + "/api/ps", timeout=10) as r:
        return json.load(r).get("models", [])


def unload():
    ollama("stop", h.MODEL)
    deadline = time.time() + 30
    while time.time() < deadline and loaded_models():
        time.sleep(0.5)
    return not loaded_models()


def base_problems(r, ctx=None):
    problems = []
    term = r.terminal
    if r.status != 200:
        problems.append(f"status {r.status}")
    if not r.events or r.events[0][0] != "started":
        problems.append("first event is not started")
    if len(term) != 1 or term[0][0] != "completed":
        problems.append(f"terminal events {term} {r.error}")
    if not r.text.strip():
        problems.append("empty answer")
    low = r.text.lower()
    if "<think>" in low or "</think>" in low or '{"answer"' in r.text:
        problems.append("model internals or JSON scaffolding reached the user")
    if r.framing_violations:
        problems.append(f"stray lines in the SSE body {r.framing_violations[:2]}")
    return problems


CASES = []


def case(cid, group, title, expected, prompt=None, check=None, custom=None, timeout=180):
    CASES.append({"id": cid, "group": group, "title": title, "expected": expected, "prompt": prompt, "check": check, "custom": custom, "timeout": timeout})


def simple(prompt, extra=None):
    def check(r):
        problems = base_problems(r)
        if extra:
            problems += extra(r)
        return problems
    return check


STRUCT = "completed ครั้งเดียว ไม่มีเศษ JSON/ความคิดของโมเดล ข้อความไม่ว่าง"
case("L-N-thai-1", "3.1", "ภาษาไทย 1", STRUCT + " และคำตอบมีอักษรไทย", "ประเทศไทยมีกี่จังหวัด ตอบสั้น ๆ", simple(None, lambda r: [] if THAI.search(r.text) else ["answer has no Thai characters"]))
case("L-N-thai-2", "3.1", "ภาษาไทย 2", STRUCT + " และคำตอบมีอักษรไทย", "ช่วยอธิบายว่า Local AI คืออะไร ใน 2 ประโยค", simple(None, lambda r: [] if THAI.search(r.text) else ["answer has no Thai characters"]))
case("L-N-english-1", "3.1", "ภาษาอังกฤษ 1", STRUCT, "What is the capital of Japan? Answer briefly.", simple(None))
case("L-N-english-2", "3.1", "ภาษาอังกฤษ 2", STRUCT, "Explain what an API is in two sentences.", simple(None))
case("L-N-emoji-1", "3.1", "emoji ในคำถาม 1", STRUCT, "Reply with a short cheerful greeting that includes a smiley emoji \U0001F600.", simple(None))
case("L-N-emoji-2", "3.1", "emoji ในคำถาม 2", STRUCT, "ตอบสั้น ๆ พร้อมอีโมจิ \U0001F680 ว่าพร้อมทำงานแล้ว", simple(None))
case("L-N-multiline-1", "3.1", "ข้อความหลายบรรทัด 1", STRUCT, "Question line one.\nQuestion line two: what is 2 + 2?\n\nAnswer briefly.", simple(None))
case("L-N-multiline-2", "3.1", "ข้อความหลายบรรทัด 2", STRUCT, "บรรทัดแรก\nบรรทัดสอง: หนึ่งบวกหนึ่งได้เท่าไร\nตอบสั้น ๆ", simple(None))
case("L-N-special-1", "3.1", "อักขระพิเศษ 1", STRUCT, 'Repeat these exactly in your answer: "quoted" <b>tag</b> & ampersand.', simple(None))
case("L-N-special-2", "3.1", "อักขระพิเศษ 2", STRUCT, "Write the HTML tag <script> as plain text in a sentence, and mention the symbol &.", simple(None))
case("L-N-short-1", "3.1", "คำตอบสั้น 1", STRUCT, "What is 7 times 6? Only the number.", simple(None))
case("L-N-short-2", "3.1", "คำตอบสั้น 2", STRUCT, "หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ", simple(None))
case("L-N-medium-1", "3.1", "คำตอบขนาดกลาง 1", STRUCT, "Describe how photosynthesis works in about 80 words.", simple(None))
case("L-N-medium-2", "3.1", "คำตอบขนาดกลาง 2", STRUCT, "อธิบายวงจรน้ำในธรรมชาติประมาณ 5 ประโยค", simple(None))
case("L-N-long-1", "3.1", "คำตอบยาว 1", STRUCT + " (หรือ error AI_RESPONSE_TOO_LARGE ถ้าเกินขีดจำกัด)", "Write an essay of about 300 words about the history of the bicycle.",
     None, custom="long")
case("L-N-long-2", "3.1", "คำตอบยาว 2", STRUCT + " (หรือ error AI_RESPONSE_TOO_LARGE ถ้าเกินขีดจำกัด)", "เขียนบทความประมาณ 250 คำ เกี่ยวกับประโยชน์ของการออกกำลังกาย", None, custom="long")
case("L-N-cold-1", "3.1", "cold start 1: unload แล้วถาม", STRUCT + " และ started มาก่อน record แรก", "What is the capital of France? Answer briefly.", None, custom="cold")
case("L-N-cold-2", "3.1", "cold start 2", STRUCT + " และ started มาก่อน record แรก", "ตอบสั้น ๆ: เมืองหลวงของญี่ปุ่นคืออะไร", None, custom="cold")


def run_long(backend_port, prompt):
    r = h.post_stream(backend_port, f"{prompt} {CANARY}", read_timeout=200)
    term = r.terminal
    if len(term) == 1 and term[0][0] == "completed":
        return base_problems(r), r
    if len(term) == 1 and term[0][0] == "error" and term[0][1].get("code") == "AI_RESPONSE_TOO_LARGE" and r.text.strip():
        return [], r  # an allowed outcome: the limit was reached and the user is told
    return [f"terminal events {term} {r.error}"], r


def run_cold(backend_port, prompt):
    problems = []
    if not unload():
        problems.append("model did not unload")
    r = h.post_stream(backend_port, f"{prompt} {CANARY}", read_timeout=120)
    problems += base_problems(r)
    started = r.first("started")
    first_delta = r.first("delta")
    if started is None or started > 1.5:
        problems.append(f"started arrived after {started} s")
    if first_delta is None or first_delta > 40:
        problems.append(f"first delta after {first_delta} s")
    return problems, r


def custom_runner(c, port):
    if c["custom"] == "long":
        return run_long(port, c["prompt"])
    if c["custom"] == "cold":
        return run_cold(port, c["prompt"])
    raise RuntimeError(c["custom"])


# cancellation on the real model
def cancel_case(point, label, n):
    def run(port):
        problems = []
        r = h.post_stream(port, f"Write a very long essay of 700 words about the history of computing. {CANARY}", close_after=point, read_timeout=120)
        if r.closed_by_client_at is None:
            problems.append("the client never reached the point to close")
        if r.terminal and r.terminal[0][0] == "completed":
            problems.append("completed after cancel")
        t_close = r.closed_by_client_at or time.monotonic()
        nxt = h.post_stream(port, f"What is 2 + 2? Only the number. {CANARY}", read_timeout=60)
        wait_first = None if nxt.first("delta") is None else nxt.first("delta")
        if not h.ended_with(nxt, "completed"):
            problems.append(f"next request ended with {nxt.terminal} {nxt.error}")
        if wait_first is None or wait_first > 3.0:
            problems.append(f"next request needed {wait_first} s for its first delta (limit 3 s)")
        return problems, {"next_first_delta_s": None if wait_first is None else round(wait_first, 2), "since_close_s": round(time.monotonic() - t_close, 2)}
    return run


for n in (1, 2):
    case(f"L-C-first-delta-{n}", "3.3", "ตัด connection หลัง delta แรก (โมเดลจริง)", "ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที", custom=cancel_case("first_delta", "", n))
    case(f"L-C-started-{n}", "3.3", "ตัด connection หลัง started (โมเดลจริง)", "ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที", custom=cancel_case("started", "", n))
    case(f"L-C-1s-{n}", "3.3", "ตัด connection ที่ 1 วินาที (โมเดลจริง)", "ไม่ completed และคำขอถัดไปได้ delta แรก ≤ 3 วินาที", custom=cancel_case(1.0, "", n))


def concurrency_case(count):
    def run(port):
        tokens = [f"ZQ{count}K{i}W" for i in range(count)]
        results = [None] * count

        def one(i):
            results[i] = h.post_stream(port, f"Reply in one short sentence that contains the word {tokens[i]}. {CANARY}", read_timeout=300)

        threads = [threading.Thread(target=one, args=(i,)) for i in range(count)]
        t0 = time.monotonic()
        [t.start() for t in threads]
        [t.join() for t in threads]
        problems, summary = [], []
        for i, r in enumerate(results):
            problems += [f"request {i}: {p}" for p in base_problems(r)]
            others = [t for j, t in enumerate(tokens) if j != i]
            if any(o in r.text for o in others):
                problems.append(f"request {i} contains the token of another request")
            summary.append({"i": i, "contains_own_token": tokens[i] in r.text, "first_delta_s": None if r.first("delta") is None else round(r.first("delta"), 2),
                            "completed_s": None if not r.events else round(r.events[-1][2], 2)})
        return problems, {"requests": count, "wall_s": round(time.monotonic() - t0, 1), "results": summary}
    return run


for count in (2, 3, 5):
    for n in (1, 2):
        case(f"L-K-{count}-{n}", "3.5", f"{count} คำขอพร้อมกันบนโมเดลจริง", "ทุกคำขอ completed ไม่มี token ของคำขออื่นในคำตอบ", custom=concurrency_case(count), timeout=400)


def page_and_regression(kind):
    def run(port):
        problems = []
        if kind == "chat":
            status, body = h.post_json(port, "/api/chat", {"message": "What is 2 + 2? Only the number."})
            data = json.loads(body) if status == 200 else {}
            if status != 200 or not str(data.get("response", "")).strip() or data.get("model") != h.MODEL:
                problems.append(f"{status} {body[:120]}")
            if "answer" in str(data.get("response", "")) and "{" in str(data.get("response", "")):
                problems.append("nested JSON in the response")
        else:
            status, body = h.post_json(port, "/api/chat", {"message": "ตอบสั้น ๆ: หนึ่งบวกหนึ่งได้เท่าไร"})
            if status != 200 or not THAI.search(body) and "2" not in body:
                problems.append(f"{status} {body[:120]}")
        return problems, {"status": status}
    return run


case("L-R-chat-1", "regression", "POST /api/chat เดิมกับโมเดลจริง", "200 ตอบถูกรูปแบบ ไม่มี JSON ซ้อน", custom=page_and_regression("chat"))
case("L-R-chat-2", "regression", "POST /api/chat ภาษาไทยกับโมเดลจริง", "200 มีคำตอบ", custom=page_and_regression("thai"))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--output", default=str(h.ROOT / "docs" / "qa" / "qa-001-live-evidence.json"))
    ap.add_argument("--repeats", type=int, default=REPEATS)
    ap.add_argument("--only")
    args = ap.parse_args(argv)
    if not h.DLL.exists():
        print("Build the Backend first (dotnet build BC-LocalAI.slnx)", file=sys.stderr)
        return 2
    try:
        loaded = loaded_models()  # also proves Ollama answers on loopback
        version = json.load(__import__("urllib.request").request.urlopen(OLLAMA + "/api/version", timeout=10))["version"]
    except Exception as exc:
        print(f"Ollama is not reachable on {OLLAMA}: {exc}", file=sys.stderr)
        return 2
    log_dir = Path(os.environ.get("TEMP", ".")) / "qa001-live-logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    port = 18700 + (os.getpid() % 200)
    backend = h.Backend(port, 11434, "live", timeout=180, log_dir=log_dir)
    results = []
    started = time.time()
    try:
        for c in CASES:
            if args.only and args.only not in c["id"]:
                continue
            runs = []
            for _ in range(args.repeats):
                t0 = time.monotonic()
                try:
                    if c["custom"] and callable(c["custom"]):
                        problems, actual = c["custom"](port)
                    elif c["custom"]:
                        problems, r = custom_runner(c, port)
                        actual = {"terminal": r.terminal, "text_preview": r.text[:80], "started_s": r.first("started"), "first_delta_s": r.first("delta")}
                    else:
                        r = h.post_stream(port, f"{c['prompt']} {CANARY}", read_timeout=c["timeout"])
                        problems = c["check"](r)
                        actual = {"terminal": r.terminal, "text_preview": r.text[:80], "started_s": r.first("started"), "first_delta_s": r.first("delta"),
                                  "completed_s": None if not r.events else round(r.events[-1][2], 2)}
                except Exception as exc:
                    problems, actual = [f"harness error {type(exc).__name__}: {exc}"], {}
                runs.append({"passed": not problems, "problems": problems, "actual": actual, "elapsed_s": round(time.monotonic() - t0, 2)})
            passed = all(x["passed"] for x in runs)
            results.append({"id": c["id"], "group": c["group"], "title": c["title"], "expected": c["expected"], "runs": runs, "passed": passed})
            print(("PASS " if passed else "FAIL ") + c["id"] + ("" if passed else "  " + "; ".join(runs[0]["problems"] or runs[-1]["problems"])[:220]))
        backend.log.flush()
        text = Path(backend.log_path).read_text(encoding="utf-8", errors="replace")
        leak = CANARY in text
        results.append({"id": "L-L-log-privacy", "group": "3.4", "title": "log ของ Backend ไม่มีคำถามหรือคำตอบ", "expected": "ไม่พบ canary ใน log",
                        "runs": [{"passed": not leak, "problems": ["canary found in the Backend log"] if leak else [], "actual": {}, "elapsed_s": 0}], "passed": not leak})
        print(("PASS " if not leak else "FAIL ") + "L-L-log-privacy")
    finally:
        backend.stop()
    out = {
        "generated_utc": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()), "ollama_version": version, "model": h.MODEL,
        "main_instance_model_loaded_at_start": bool(loaded), "repeats": args.repeats, "cases": len(results),
        "case_runs": sum(len(r["runs"]) for r in results), "passed_cases": sum(1 for r in results if r["passed"]),
        "failed_cases": [r["id"] for r in results if not r["passed"]], "duration_s": round(time.time() - started, 1), "results": results,
    }
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n{out['passed_cases']}/{out['cases']} live cases passed ({out['case_runs']} runs). Evidence: {args.output}")
    return 0 if not out["failed_cases"] else 1


if __name__ == "__main__":
    sys.exit(main())
