#!/usr/bin/env python3
"""QA-001 end-to-end streaming checks: browser-side SSE client -> Backend -> fake Ollama.

The real Backend (built output, Production environment) is started against a scripted fake
Ollama on loopback, so failures that are hard to trigger on a real model (runner crash,
stalls, bad records, oversize output, queueing) are repeatable. Expected results are
defined in streaming-cases.json-style tables below BEFORE anything runs; Actual is recorded
separately and never edited. Every case runs REPEATS times.

Not covered here (need FE-002, the browser UI): Stop button, closing the tab, rendering of
HTML in the page, queue-wait indicator. They are listed as BLOCKED in the report.

Usage (Backend must already be built: dotnet build BC-LocalAI.slnx):
    python tests/e2e/streaming_e2e.py --output docs/qa/qa-001-evidence.json
Exit code 0 when every executed case passed, 1 otherwise.
"""
from __future__ import annotations

import argparse
import http.client
import http.server
import json
import os
import uuid
import socket
import socketserver
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / "BC-LocalAI" / "bin" / "Debug" / "net10.0" / "BC-LocalAI.dll"
MODEL = "qwen3:4b"
REPEATS = 2
CANARY_PROMPT = "CANARY-PROMPT-91b7e2"
CANARY_ANSWER = "CANARY-ANSWER-4c0d3f"


# --------------------------------------------------------------------------- fake Ollama
class FakeState:
    def __init__(self):
        self.lock = threading.Lock()          # emulates OLLAMA_NUM_PARALLEL=1 (one generation at a time)
        self.events = []                      # (request marker, event name, monotonic time)
        self.event_lock = threading.Lock()

    def note(self, marker, name):
        with self.event_lock:
            self.events.append((marker, name, time.monotonic()))

    def when(self, marker, name):
        with self.event_lock:
            for m, n, t in self.events:
                if m == marker and n == name:
                    return t
        return None


def ndjson(obj) -> bytes:
    return (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")


def pieces(text: str, size: int):
    """Split the JSON text the model would stream; sizes of 1-3 split JSON escapes and Thai clusters."""
    return [text[i:i + size] for i in range(0, len(text), size)]


def answer_json(answer: str, escape_unicode=False) -> str:
    return json.dumps({"answer": answer}, ensure_ascii=escape_unicode)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    state: FakeState = None  # set by the harness

    def log_message(self, *args):
        pass

    # chunked helpers
    def chunk(self, data: bytes):
        self.wfile.write(b"%x\r\n" % len(data) + data + b"\r\n")
        self.wfile.flush()

    def end_chunks(self):
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    def start_stream(self, content_type="application/x-ndjson", status=200):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

    def crash(self):
        """Close the socket without the final chunk: what a killed runner looks like to the client."""
        try:
            self.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.close_connection = True

    def read_body(self) -> bytes:
        """The Backend may send the request body chunked (no Content-Length)."""
        if "chunked" in self.headers.get("Transfer-Encoding", "").lower():
            data = b""
            while True:
                size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()
                    return data
                data += self.rfile.read(size)
                self.rfile.readline()
        return self.rfile.read(int(self.headers.get("Content-Length", "0")))

    def do_POST(self):
        body = json.loads(self.read_body() or b"{}")
        prompt = body.get("prompt", "")
        marker = prompt.split("[[", 1)[1].split("]]", 1)[0] if "[[" in prompt else "plain"
        name, _, arg = marker.partition(":")
        self.state.note(marker, "request")
        try:
            if body.get("stream") is False:
                return self.non_stream(name)
            if SCENARIOS.get(name, {}).get("kind") in ("queue_hold", "long_run"):
                with self.state.lock:  # Ollama handles one generation at a time (NUM_PARALLEL=1)
                    self.state.note(marker, "start")
                    self.scenario(name, arg, marker)
            else:
                self.state.note(marker, "start")
                self.scenario(name, arg, marker)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.state.note(marker, "client_disconnected")
            self.close_connection = True

    # non-streaming (regression of POST /api/chat)
    def non_stream(self, name):
        if name == "ns_http500":
            payload = b'{"error":"boom"}'
            self.send_response(500)
        else:
            payload = json.dumps({"model": MODEL, "response": json.dumps({"answer": "ns ok"}), "done": True,
                                  "done_reason": "stop"}).encode()
            self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    # scripted behaviours
    def emit_answer(self, answer, size=3, delay=0.0, escape=False, model=MODEL, thinking=False, done_reason="stop"):
        for p in pieces(answer_json(answer, escape), size):
            rec = {"model": model, "created_at": "2026-01-01T00:00:00Z", "response": p, "done": False}
            if thinking:
                rec["thinking"] = "internal reasoning that must never reach the user"
            self.chunk(ndjson(rec))
            if delay:
                time.sleep(delay)
        self.chunk(ndjson({"model": model, "created_at": "2026-01-01T00:00:00Z", "response": "", "done": True,
                           "done_reason": done_reason, "eval_count": 7}))

    def scenario(self, name, arg, marker):
        s = SCENARIOS.get(name)
        if s is None:
            self.start_stream()
            self.emit_answer("plain answer " + CANARY_ANSWER)
            return self.end_chunks()
        kind = s["kind"]
        if kind == "down":
            return
        if kind == "http500":
            self.send_response(500)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", "20")
            self.end_headers()
            self.wfile.write(b'{"error":"internal"}')
            return
        if kind == "bad_content_type":
            self.start_stream("text/html")
            self.chunk(b"<html>not ndjson</html>")
            return self.end_chunks()
        if kind in ("first_record_delay", "queue_hold"):
            time.sleep(s["delay"])
        self.start_stream()
        if kind in ("answer", "first_record_delay", "queue_hold"):
            self.emit_answer(s["answer"], size=s.get("size", 3), delay=s.get("drip", 0.0),
                             escape=s.get("escape", False), thinking=s.get("thinking", False))
            return self.end_chunks()
        if kind == "length":
            self.emit_answer(s["answer"], done_reason="length")
            return self.end_chunks()
        if kind == "crash_mid":
            for p in pieces(answer_json("partial answer then the runner dies and more text follows"), 3)[:s.get("n", 4)]:
                self.chunk(ndjson({"model": MODEL, "response": p, "done": False}))
            return self.crash()
        if kind == "no_done_close":
            for p in pieces(answer_json("stream ends without done but with a longer answer"), 3)[:s.get("n", 4)]:
                self.chunk(ndjson({"model": MODEL, "response": p, "done": False}))
            return self.end_chunks()
        if kind == "invalid_json":
            self.chunk(ndjson({"model": MODEL, "response": '{"ans', "done": False}))
            self.chunk(b"this is not json at all\n")
            return self.end_chunks()
        if kind == "wrong_model":
            self.emit_answer("hello", model="other-model:1b")
            return self.end_chunks()
        if kind == "error_record":
            self.chunk(ndjson({"model": MODEL, "response": '{"answer":"' + s.get("text", "st"), "done": False}))
            self.chunk(ndjson({"error": "model runner crashed at 127.0.0.1:49152 api_key=" + "ci-fake-value-not-real"}))  # ci-allow-secret
            return self.end_chunks()
        if kind == "stall_mid":
            for p in pieces(answer_json("some words before the stall and then silence"), 3)[:s.get("n", 5)]:
                self.chunk(ndjson({"model": MODEL, "response": p, "done": False}))
            time.sleep(s["stall"])
            return
        if kind == "drip_forever":
            text = answer_json("x" * 400)
            for p in pieces(text, 2):
                self.chunk(ndjson({"model": MODEL, "response": p, "done": False}))
                time.sleep(s["drip"])
            return self.end_chunks()
        if kind == "long_run":
            # Emulates a long generation that only stops when the client disconnects.
            for i in range(int(s["seconds"] / 0.1)):
                self.chunk(ndjson({"model": MODEL, "response": '{"answer":"' if i == 0 else "word ", "done": False}))
                time.sleep(0.1)
            return self.end_chunks()
        if kind == "oversize":
            self.chunk(ndjson({"model": MODEL, "response": '{"answer":"' + "a" * 6000, "done": False}))
            return self.end_chunks()
        raise RuntimeError(kind)


THAI = "สวัสดีครับ ยินดีต้อนรับสู่ระบบ AI ภายในบริษัท " + CANARY_ANSWER
LONG_TEXT = ("ประโยคที่ยาวมากเพื่อทดสอบการแบ่งข้อมูลหลายชิ้น " * 60).strip()
SCENARIOS = {
    # 3.1 normal
    "n_thai": {"kind": "answer", "answer": THAI},
    "n_thai_b": {"kind": "answer", "answer": "ทดสอบข้อความภาษาไทยที่มีวรรณยุกต์ ก่อ ก๊อ กี้ " + CANARY_ANSWER, "size": 1},
    "n_english": {"kind": "answer", "answer": "Hello, this is an internal AI answer. " + CANARY_ANSWER},
    "n_english_b": {"kind": "answer", "answer": "A second English answer with punctuation; commas, and a colon: ok.", "size": 2},
    "n_emoji": {"kind": "answer", "answer": "Done \U0001F600 \U0001F680 ok " + CANARY_ANSWER, "size": 1},
    "n_emoji_escaped": {"kind": "answer", "answer": "Escaped \U0001F600 pair", "escape": True, "size": 1},
    "n_multiline": {"kind": "answer", "answer": "line one\nline two\n\nline four"},
    "n_multiline_b": {"kind": "answer", "answer": "a\r\nb\tc\nd", "size": 1},
    "n_special": {"kind": "answer", "answer": 'quote " less < amp & backslash \\ end'},
    "n_special_b": {"kind": "answer", "answer": "tag <b>bold</b> & entity &amp; slash / end", "size": 2},
    "n_short": {"kind": "answer", "answer": "4"},
    "n_short_b": {"kind": "answer", "answer": "ok"},
    "n_medium": {"kind": "answer", "answer": "word " * 80},
    "n_medium_b": {"kind": "answer", "answer": "ข้อความปานกลาง " * 30, "size": 5},
    "n_long": {"kind": "answer", "answer": LONG_TEXT, "size": 7},
    "n_long_b": {"kind": "answer", "answer": "long english sentence " * 150, "size": 11},
    "n_cold": {"kind": "first_record_delay", "delay": 2.0, "answer": "after a cold load"},
    "n_cold_b": {"kind": "first_record_delay", "delay": 1.5, "answer": "ตอบหลังโหลดโมเดล"},
    "n_thinking_field": {"kind": "answer", "answer": "visible answer only", "thinking": True},
    "n_thinking_field_b": {"kind": "answer", "answer": "คำตอบที่เห็นได้เท่านั้น", "thinking": True},
    # 3.2 errors
    "e_down": {"kind": "down"},
    "e_http500": {"kind": "http500"},
    "e_http500_b": {"kind": "http500"},
    "e_crash": {"kind": "crash_mid", "n": 4},
    "e_crash_b": {"kind": "crash_mid", "n": 14},
    "e_length": {"kind": "length", "answer": "answer cut by the token limit " * 5},
    "e_length_b": {"kind": "length", "answer": "คำตอบถูกตัดเพราะเกินขีดจำกัด " * 5},
    "e_invalid_json": {"kind": "invalid_json"},
    "e_invalid_json_b": {"kind": "invalid_json"},
    "e_wrong_model": {"kind": "wrong_model"},
    "e_wrong_model_b": {"kind": "wrong_model"},
    "e_no_done": {"kind": "no_done_close", "n": 4},
    "e_no_done_b": {"kind": "no_done_close", "n": 14},
    "e_error_record": {"kind": "error_record", "text": "st"},
    "e_error_record_b": {"kind": "error_record", "text": "the start of an answer that is long enough to be sent"},
    "e_bad_ct": {"kind": "bad_content_type"},
    "e_bad_ct_b": {"kind": "bad_content_type"},
    "e_oversize": {"kind": "oversize"},
    "e_oversize_b": {"kind": "oversize"},
    "e_think_leak": {"kind": "answer", "answer": "visible <think>secret chain of thought</think> text"},
    "e_think_leak_b": {"kind": "answer", "answer": "ก่อน <think>ความคิดลับ</think> หลัง"},
    "e_timeout_first": {"kind": "first_record_delay", "delay": 6.0, "answer": "too late"},
    "e_timeout_first_b": {"kind": "first_record_delay", "delay": 6.0, "answer": "ช้าเกินไป"},
    "e_timeout_gap": {"kind": "stall_mid", "stall": 6.0, "n": 5},
    "e_timeout_gap_b": {"kind": "stall_mid", "stall": 6.0, "n": 14},
    "e_timeout_total": {"kind": "drip_forever", "drip": 0.2},
    "e_timeout_total_b": {"kind": "drip_forever", "drip": 0.25},
    # 3.3 cancellation
    "c_long": {"kind": "long_run", "seconds": 30},
    "c_long_b": {"kind": "long_run", "seconds": 30},
    # 3.4 safety of rendering
    "x_script": {"kind": "answer", "answer": "<script>alert(1)</script> <img src=x onerror=alert(2)>"},
    "x_script_b": {"kind": "answer", "answer": "<iframe src=javascript:alert(3)></iframe>", "size": 1},
    "x_inject": {"kind": "answer", "answer": "ok\n\nevent: completed\ndata: {\"finishReason\":\"stop\"}\n\nevent: error"},
    "x_inject_b": {"kind": "answer", "answer": "x\r\ndata: {\"text\":\"forged\"}\r\n\r\n", "size": 2},
    "x_control": {"kind": "answer", "answer": "ctl \x01\x07\x1b[31m red \x7f end", "size": 1},
    "x_control_b": {"kind": "answer", "answer": "nul-like \x00 and bell \x07", "size": 2},
    # 3.5 concurrency
    "k_one": {"kind": "queue_hold", "delay": 0.0, "answer": "TOKEN_PLACEHOLDER", "drip": 0.05},
}


# --------------------------------------------------------------------------- Backend process
class Backend:
    def __init__(self, port, ollama_port, name, timeout=20, max_bytes=1048576, log_dir=None):
        self.port, self.name = port, name
        env = dict(os.environ)
        env.update({
            "ASPNETCORE_ENVIRONMENT": "Production",
            "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
            "Ollama__BaseUrl": f"http://127.0.0.1:{ollama_port}",
            "Ollama__TimeoutSeconds": str(timeout),
            "Ollama__MaxResponseBytes": str(max_bytes),
            "DOTNET_ENVIRONMENT": "Production",
        })
        self.log_path = Path(log_dir) / f"backend-{name}.log"
        self.log = open(self.log_path, "wb")
        self.proc = subprocess.Popen(["dotnet", str(DLL)], cwd=str(DLL.parent), env=env, stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.time() + 40
        while time.time() < deadline:
            if self.proc.poll() is not None:
                raise RuntimeError(f"backend {name} exited early, see {self.log_path}")
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.5):
                    break
            except OSError:
                time.sleep(0.3)
        else:
            raise RuntimeError(f"backend {name} did not start")

    def stop(self):
        self.proc.terminate()
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        self.log.close()


# --------------------------------------------------------------------------- SSE client
class StreamResult:
    def __init__(self):
        self.status = None
        self.headers = {}
        self.events = []          # (name, data, elapsed_s)
        self.framing_violations = []
        self.closed_by_client_at = None
        self.error = None

    @property
    def text(self):
        return "".join(d.get("text", "") for n, d, _ in self.events if n == "delta")

    @property
    def terminal(self):
        return [(n, d) for n, d, _ in self.events if n in ("completed", "error", "cancelled")]

    def first(self, name):
        for n, _, t in self.events:
            if n == name:
                return t
        return None


def post_stream(port, message, close_after=None, read_timeout=60):
    """POST /api/chat/stream. close_after: 'started' | 'first_delta' | float seconds, else read to the end."""
    result = StreamResult()
    started = time.monotonic()
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=read_timeout)
    body = json.dumps({"message": message}, ensure_ascii=False).encode("utf-8")
    try:
        conn.request("POST", "/api/chat/stream", body=body, headers={"Content-Type": "application/json", "Accept": "text/event-stream"})
        resp = conn.getresponse()
        result.status = resp.status
        result.headers = {k.lower(): v for k, v in resp.getheaders()}
        if resp.status != 200:
            result.error = resp.read().decode("utf-8", "replace")
            return result
        name = None
        pending_blank = True
        while True:
            if isinstance(close_after, float) and time.monotonic() - started >= close_after:
                result.closed_by_client_at = time.monotonic()
                break
            raw = resp.readline()
            if not raw:
                break
            line = raw.decode("utf-8").rstrip("\r\n")
            if line == "":
                pending_blank = True
                continue
            if line.startswith("event: "):
                name = line[7:]
            elif line.startswith("data: "):
                result.events.append((name, json.loads(line[6:]), time.monotonic() - started))
                if close_after == "started" and name == "started":
                    result.closed_by_client_at = time.monotonic()
                    break
                if close_after == "first_delta" and name == "delta":
                    result.closed_by_client_at = time.monotonic()
                    break
            else:
                result.framing_violations.append(line)
    except (socket.timeout, OSError, http.client.HTTPException, json.JSONDecodeError) as exc:
        result.error = f"{type(exc).__name__}: {exc}"
    finally:
        conn.close()
    return result


def ended_with(r, name):
    return bool(r.terminal) and r.terminal[0][0] == name


def get_page_headers(port):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
    conn.request("GET", "/")
    resp = conn.getresponse()
    resp.read()
    headers = {k.lower(): v for k, v in resp.getheaders()}
    conn.close()
    return resp.status, headers


def post_json(port, path, payload):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=60)
    conn.request("POST", path, body=json.dumps(payload, ensure_ascii=False).encode("utf-8"), headers={"Content-Type": "application/json"})
    resp = conn.getresponse()
    text = resp.read().decode("utf-8", "replace")
    conn.close()
    return resp.status, text


# --------------------------------------------------------------------------- cases
def expected_text(scn):
    s = SCENARIOS[scn]
    return s["answer"]


def check_normal(scn):
    def check(r, ctx):
        problems = []
        if r.status != 200:
            problems.append(f"status {r.status}")
        if not r.events or r.events[0][0] != "started":
            problems.append("first event is not started")
        term = r.terminal
        if len(term) != 1 or term[0][0] != "completed":
            problems.append(f"terminal events {term}")
        if r.text != expected_text(scn):
            problems.append("text differs from the answer the model produced")
        if r.framing_violations:
            problems.append(f"stray lines in the SSE body: {r.framing_violations[:2]}")
        if "<think>" in r.text.lower() or '"answer"' in r.text or "internal reasoning" in r.text:
            problems.append("model internals or JSON scaffolding reached the user")
        return problems
    return check


def check_error(code, partial=None, forbid=()):
    def check(r, ctx):
        problems = []
        term = r.terminal
        if r.status != 200:
            problems.append(f"status {r.status}")
        if len(term) != 1 or term[0][0] != "error":
            problems.append(f"terminal events {term}")
        else:
            data = term[0][1]
            if data.get("code") != code:
                problems.append(f"code {data.get('code')} instead of {code}")
            if partial is not None and data.get("partial") is not partial:
                problems.append(f"partial={data.get('partial')} instead of {partial}")
            blob = json.dumps(data)
            for bad in ("127.0.0.1", "49152", "api_key", "ci-fake", "Exception", "   at ", "runner"):
                if bad in blob:
                    problems.append(f"internal detail '{bad}' in the error event")
        if any(n == "completed" for n, _, _ in r.events):
            problems.append("completed was sent although the stream failed")
        for word in forbid:
            if word in r.text:
                problems.append(f"'{word}' reached the user")
        if r.framing_violations:
            problems.append(f"stray lines in the SSE body: {r.framing_violations[:2]}")
        return problems
    return check


def check_xss(scn):
    base = check_normal(scn)

    def check(r, ctx):
        problems = base(r, ctx)
        ctype = r.headers.get("content-type", "")
        if not ctype.startswith("text/event-stream"):
            problems.append(f"content type {ctype}")
        # Event injection: only one terminal event and no forged delta/event from the answer text.
        names = [n for n, _, _ in r.events]
        if names.count("started") != 1 or names.count("completed") != 1 or "error" in names:
            problems.append(f"event names {names}")
        return problems
    return check


def run_case(case, backends, fake):
    marker = f"[[{case['scn']}]]"
    prompt = f"{CANARY_PROMPT} {marker} ตอบสั้น ๆ"
    ctx = {"fake": fake, "marker": case["scn"]}
    port = backends[case.get("backend", "main")].port
    t0 = time.monotonic()
    if case.get("custom"):
        problems, actual = case["custom"](backends, fake, case)
    else:
        r = post_stream(port, prompt, close_after=case.get("close_after"))
        problems = case["check"](r, ctx)
        actual = {"status": r.status, "events": [n for n, _, _ in r.events][:12], "terminal": r.terminal,
                  "text_preview": r.text[:80], "transport_error": r.error}
    return {"passed": not problems, "problems": problems, "actual": actual, "elapsed_s": round(time.monotonic() - t0, 2)}


def custom_cancel(point):
    def run(backends, fake, case):
        marker = f"{case['scn']}:{uuid.uuid4().hex[:8]}"
        port = backends["main"].port
        prompt = f"{CANARY_PROMPT} [[{marker}]] ตอบยาว ๆ"
        r = post_stream(port, prompt, close_after=point)
        problems = []
        if r.closed_by_client_at is None:
            problems.append("the client never reached the point where it closes")
            return problems, {"events": [n for n, _, _ in r.events]}
        if r.terminal and r.terminal[0][0] == "completed":
            problems.append("completed sent after the client cancelled")
        # Backend must tell Ollama to stop: the fake sees the disconnect soon after the client closed.
        deadline = time.monotonic() + 5
        seen = None
        while time.monotonic() < deadline:
            seen = fake.when(marker, "client_disconnected")
            if seen is not None:
                break
            time.sleep(0.05)
        detect_s = None if seen is None else round(seen - r.closed_by_client_at, 2)
        if seen is None and fake.when(marker, "start") is None:
            detect_s = 0.0  # the request never reached Ollama: nothing was left generating
        elif seen is None:
            problems.append("Ollama was still generating 5 s after the client closed")
        elif detect_s is not None and detect_s > 1.5:
            problems.append(f"Ollama stopped {detect_s}s after the client closed (limit 1.5s)")
        # The next request must start without waiting for the cancelled generation (it holds the lock).
        nxt = post_stream(port, f"{CANARY_PROMPT} [[n_short]] next")
        first_delta = nxt.first("delta")
        if not ended_with(nxt, "completed"):
            problems.append(f"next request ended with {nxt.terminal}")
        if first_delta is None or first_delta > 3.0:
            problems.append(f"next request waited {first_delta} s for its first delta")
        return problems, {"disconnect_detected_s": detect_s, "next_first_delta_s": None if first_delta is None else round(first_delta, 2)}
    return run


def custom_concurrency(n):
    def run(backends, fake, case):
        port = backends["main"].port
        # A dedicated scenario per request would break the lock ordering, so each prompt carries its own token.
        results = [None] * n
        tokens = [f"TOK{n}X{i}Z" for i in range(n)]

        def one(i):
            SCENARIOS[f"k_{tokens[i]}"] = {"kind": "queue_hold", "delay": 0.0, "answer": f"{tokens[i]} " * 12, "drip": 0.03, "size": 4}
            results[i] = post_stream(port, f"{CANARY_PROMPT} [[k_{tokens[i]}]] go", read_timeout=90)

        threads = [threading.Thread(target=one, args=(i,)) for i in range(n)]
        t0 = time.monotonic()
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        problems, summary = [], []
        for i, r in enumerate(results):
            others = [t for j, t in enumerate(tokens) if j != i]
            text = r.text
            if not ended_with(r, "completed"):
                problems.append(f"request {i} ended with {r.terminal} {r.error}")
            if text != (tokens[i] + " ") * 12:
                problems.append(f"request {i} text is not exactly its own answer")
            if any(o in text for o in others):
                problems.append(f"request {i} contains text of another request")
            summary.append({"i": i, "terminal": [t for t, _ in r.terminal], "first_delta_s": None if r.first("delta") is None else round(r.first("delta"), 2)})
        return problems, {"requests": n, "wall_s": round(time.monotonic() - t0, 2), "results": summary}
    return run


def custom_queue_full(backends, fake, case):
    """Backend timeout is 3 s; the first request holds the (serial) fake for 5 s, the second must time out."""
    port = backends["short"].port
    SCENARIOS["q_hold"] = {"kind": "queue_hold", "delay": 5.0, "answer": "held request"}
    SCENARIOS["q_wait"] = {"kind": "queue_hold", "delay": 0.0, "answer": "never produced"}
    out = {}

    def first():
        out["first"] = post_stream(port, f"{CANARY_PROMPT} [[q_hold]] a", read_timeout=30)

    t = threading.Thread(target=first)
    t.start()
    time.sleep(0.8)
    second = post_stream(port, f"{CANARY_PROMPT} [[q_wait]] b", read_timeout=30)
    t.join()
    with fake.lock:  # the held generation must finish before the next case starts
        pass
    problems = []
    term = second.terminal
    if len(term) != 1 or term[0][0] != "error" or term[0][1].get("code") != "AI_TIMEOUT":
        problems.append(f"queued request ended with {term}")
    elif term[0][1].get("partial") is not False:
        problems.append("queued request reported partial output")
    if second.text:
        problems.append("queued request received text")
    return problems, {"second_terminal": term, "second_elapsed_s": round(second.events[-1][2], 2) if second.events else None}


def custom_page_csp(backends, fake, case):
    status, headers = get_page_headers(backends["main"].port)
    csp = headers.get("content-security-policy", "")
    problems = []
    if status != 200:
        problems.append(f"GET / returned {status}")
    for needle in ("default-src 'self'", "script-src 'self'", "connect-src 'self'", "object-src 'none'", "frame-ancestors 'none'"):
        if needle not in csp:
            problems.append(f"CSP lacks {needle}")
    if "unsafe-inline" in csp or "unsafe-eval" in csp:
        problems.append("CSP allows unsafe-inline/unsafe-eval")
    if headers.get("x-content-type-options") != "nosniff":
        problems.append("X-Content-Type-Options is not nosniff")
    return problems, {"status": status, "csp": csp, "nosniff": headers.get("x-content-type-options")}


def custom_stream_headers(backends, fake, case):
    r = post_stream(backends["main"].port, f"{CANARY_PROMPT} [[n_short]] h")
    problems = []
    h = r.headers
    if not h.get("content-type", "").startswith("text/event-stream"):
        problems.append(f"content type {h.get('content-type')}")
    if h.get("cache-control") != "no-cache":
        problems.append(f"cache-control {h.get('cache-control')}")
    if h.get("x-accel-buffering") != "no":
        problems.append(f"x-accel-buffering {h.get('x-accel-buffering')}")
    return problems, {"headers": {k: h.get(k) for k in ("content-type", "cache-control", "x-accel-buffering")}}


def custom_started_before_first_record(backends, fake, case):
    r = post_stream(backends["main"].port, f"{CANARY_PROMPT} [[n_cold]] cold")
    problems = []
    started_at = r.first("started")
    first_delta = r.first("delta")
    if started_at is None or started_at > 1.0:
        problems.append(f"started arrived after {started_at} s (must not wait for Ollama)")
    if first_delta is None or first_delta < 1.5:
        problems.append("the fake did not delay the first record as designed")
    return problems, {"started_s": None if started_at is None else round(started_at, 2), "first_delta_s": None if first_delta is None else round(first_delta, 2)}


def custom_validation(backends, fake, case):
    port = backends["main"].port
    problems, seen = [], []
    for label, payload in (("blank", {"message": "   "}), ("missing", {}), ("wrong type", {"message": 5})):
        conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
        conn.request("POST", "/api/chat/stream", body=json.dumps(payload).encode(), headers={"Content-Type": "application/json"})
        resp = conn.getresponse()
        body = resp.read().decode()
        conn.close()
        seen.append((label, resp.status))
        if resp.status != 400 or "INVALID_MESSAGE" not in body:
            problems.append(f"{label}: {resp.status} {body[:80]}")
        if resp.getheader("Content-Type", "").startswith("text/event-stream"):
            problems.append(f"{label}: answered as an event stream")
    return problems, {"responses": seen}


def custom_regression(variant):
    def run(backends, fake, case):
        port = backends["main"].port
        problems = []
        if variant == "ok":
            status, body = post_json(port, "/api/chat", {"message": f"[[ns_ok]] hi"})
            data = json.loads(body) if status == 200 else {}
            if status != 200 or data.get("response") != "ns ok" or data.get("model") != MODEL:
                problems.append(f"{status} {body[:100]}")
        elif variant == "empty":
            status, body = post_json(port, "/api/chat", {"message": ""})
            if status != 400 or "INVALID_MESSAGE" not in body:
                problems.append(f"{status} {body[:100]}")
        elif variant == "provider_error":
            status, body = post_json(port, "/api/chat", {"message": "[[ns_http500]] x"})
            if status != 502 or "AI_PROVIDER_ERROR" not in body:
                problems.append(f"{status} {body[:100]}")
        elif variant == "thai":
            status, body = post_json(port, "/api/chat", {"message": "[[ns_ok]] สวัสดี"})
            if status != 200 or "ns ok" not in body:
                problems.append(f"{status} {body[:100]}")
        return problems, {"variant": variant}
    return run


def build_cases():
    cases = []

    def add(cid, group, title, owner, expected, **kw):
        cases.append({"id": cid, "group": group, "title": title, "owner": owner, "expected": expected, **kw})

    # 3.1 normal
    normal = [
        ("n_thai", "ภาษาไทย (ตัวอักษรแบ่งกลางชิ้นข้อมูล)"), ("n_thai_b", "ภาษาไทยมีวรรณยุกต์ (ทีละอักขระ)"),
        ("n_english", "ภาษาอังกฤษ"), ("n_english_b", "ภาษาอังกฤษมีเครื่องหมายวรรคตอน"),
        ("n_emoji", "emoji (คู่ surrogate แบ่งทีละอักขระ)"), ("n_emoji_escaped", "emoji ที่มาเป็น \\uXXXX"),
        ("n_multiline", "หลายบรรทัดและบรรทัดว่าง"), ("n_multiline_b", "CRLF และ tab"),
        ("n_special", "อักขระพิเศษ \" < & \\"), ("n_special_b", "แท็ก HTML และ entity เป็นข้อความ"),
        ("n_short", "คำตอบสั้นมาก"), ("n_short_b", "คำตอบสั้น 2 ตัวอักษร"),
        ("n_medium", "คำตอบขนาดกลาง"), ("n_medium_b", "คำตอบขนาดกลางภาษาไทย"),
        ("n_long", "คำตอบยาว (ภาษาไทย)"), ("n_long_b", "คำตอบยาว (ภาษาอังกฤษ)"),
        ("n_thinking_field", "ฟิลด์ thinking ต้องไม่ถึงผู้ใช้"), ("n_thinking_field_b", "ฟิลด์ thinking กับภาษาไทย"),
    ]
    for scn, title in normal:
        add(f"QA1-N-{scn}", "3.1", title, "Backend", "completed ครั้งเดียว ข้อความตรงกับคำตอบ ไม่มีเศษ JSON/ความคิด", scn=scn, check=check_normal(scn))
    add("QA1-N-cold", "3.1", "cold start: started ถึงก่อน record แรกของ Ollama", "Backend",
        "started ≤ 1 วินาที ทั้งที่ Ollama ตอบช้า 2 วินาที", scn="n_cold", custom=custom_started_before_first_record)
    add("QA1-N-cold-b", "3.1", "cold start ภาษาไทย", "Backend", "completed และข้อความตรง", scn="n_cold_b", check=check_normal("n_cold_b"))
    add("QA1-N-headers", "3.1", "header ของสตรีม", "Backend", "text/event-stream, no-cache, X-Accel-Buffering: no", scn="n_short", custom=custom_stream_headers)
    add("QA1-N-validation", "3.1", "ข้อความว่าง/ไม่มี/ผิดชนิด", "Backend", "400 INVALID_MESSAGE ก่อนเริ่มสตรีม", scn="n_short", custom=custom_validation)

    # 3.2 errors
    err = [
        ("e_down", "main_down", "Ollama ไม่ทำงาน", "AI_UNAVAILABLE", False),
        ("e_http500", "main", "Ollama ตอบ HTTP 500", "AI_PROVIDER_ERROR", False), ("e_http500_b", "main", "Ollama ตอบ HTTP 500 (ซ้ำ)", "AI_PROVIDER_ERROR", False),
        ("e_crash", "main", "runner ล้มก่อนมีข้อความถึงผู้ใช้", "AI_INVALID_RESPONSE", False), ("e_crash_b", "main", "runner ล้มหลังส่งข้อความไปแล้ว", "AI_INVALID_RESPONSE", True),
        ("e_length", "main", "คำตอบถูกตัด (length)", "AI_RESPONSE_TOO_LARGE", True), ("e_length_b", "main", "คำตอบถูกตัดภาษาไทย", "AI_RESPONSE_TOO_LARGE", True),
        ("e_invalid_json", "main", "บรรทัดที่ไม่ใช่ JSON", "AI_INVALID_RESPONSE", False), ("e_invalid_json_b", "main", "บรรทัดที่ไม่ใช่ JSON (ซ้ำ)", "AI_INVALID_RESPONSE", False),
        ("e_wrong_model", "main", "โมเดลในตอบไม่ตรง", "AI_INVALID_RESPONSE", False), ("e_wrong_model_b", "main", "โมเดลไม่ตรง (ซ้ำ)", "AI_INVALID_RESPONSE", False),
        ("e_no_done", "main", "สตรีมจบโดยไม่มี done ก่อนมีข้อความ", "AI_INVALID_RESPONSE", False), ("e_no_done_b", "main", "สตรีมจบโดยไม่มี done หลังส่งข้อความ", "AI_INVALID_RESPONSE", True),
        ("e_error_record", "main", "record error ก่อนมีข้อความ (มีรายละเอียดภายใน)", "AI_PROVIDER_ERROR", False), ("e_error_record_b", "main", "record error หลังส่งข้อความ", "AI_PROVIDER_ERROR", True),
        ("e_bad_ct", "main", "Content-Type ไม่ใช่ ndjson", "AI_INVALID_RESPONSE", False), ("e_bad_ct_b", "main", "Content-Type ผิด (ซ้ำ)", "AI_INVALID_RESPONSE", False),
        ("e_oversize", "bytes", "ข้อมูลเกิน MaxResponseBytes", "AI_RESPONSE_TOO_LARGE", None), ("e_oversize_b", "bytes", "ข้อมูลเกินขนาด (ซ้ำ)", "AI_RESPONSE_TOO_LARGE", None),
        ("e_think_leak", "main", "<think> ในคำตอบ", "AI_INVALID_RESPONSE", None), ("e_think_leak_b", "main", "<think> ภาษาไทย", "AI_INVALID_RESPONSE", None),
        ("e_timeout_first", "short", "timeout: รอ record แรก", "AI_TIMEOUT", False), ("e_timeout_first_b", "short", "timeout รอ record แรก (ซ้ำ)", "AI_TIMEOUT", False),
        ("e_timeout_gap", "short", "timeout: ช่องว่างก่อนมีข้อความ", "AI_TIMEOUT", False), ("e_timeout_gap_b", "short", "timeout: ช่องว่างหลังส่งข้อความ", "AI_TIMEOUT", True),
        ("e_timeout_total", "short", "timeout: เวลารวม (หยดช้า)", "AI_TIMEOUT", True), ("e_timeout_total_b", "short", "timeout เวลารวม (ซ้ำ)", "AI_TIMEOUT", True),
    ]
    for scn, backend, title, code, partial in err:
        forbid = ("secret chain of thought", "ความคิดลับ") if scn.startswith("e_think") else ()
        add(f"QA1-E-{scn}", "3.2", title, "Backend", f"error ครั้งเดียว code={code}"
            + ("" if partial is None else f" partial={str(partial).lower()}") + " ไม่มี completed ไม่มีรายละเอียดภายใน",
            scn=scn, backend=backend, check=check_error(code, partial, forbid))
    add("QA1-E-queue-full", "3.2", "คิวเต็ม: คำขอที่รอเกิน timeout", "Backend", "คำขอที่สองได้ AI_TIMEOUT partial=false ไม่มีข้อความ",
        scn="q_wait", custom=custom_queue_full)

    # 3.3 cancellation
    for point, label in (("first_delta", "ตัด connection หลัง delta แรก"), ("started", "ตัด connection หลัง started ระหว่างรอ Ollama"), (0.8, "ตัด connection ที่ 0.8 วินาที")):
        for suffix, scn in (("", "c_long"), ("-b", "c_long_b")):
            add(f"QA1-C-{point}{suffix}", "3.3", label, "Backend",
                "Ollama หยุด generate ≤ 1.5 วินาทีหลัง client ปิด และคำขอถัดไปได้ delta แรก ≤ 3 วินาที ไม่ส่ง completed",
                scn=scn, custom=custom_cancel(point))

    # 3.4 safety
    for scn in ("x_script", "x_script_b", "x_inject", "x_inject_b", "x_control", "x_control_b"):
        add(f"QA1-X-{scn}", "3.4", f"คำตอบอันตราย: {scn}", "Backend",
            "ข้อความผ่านเป็นข้อมูล (decode JSON แล้วเท่ากับต้นฉบับ) ไม่มี event ปลอม ไม่เสียการแบ่ง event", scn=scn, check=check_xss(scn))
    add("QA1-X-csp", "3.4", "Content-Security-Policy ของหน้าแชต", "Backend", "CSP มี default/script/connect-src 'self', object-src 'none', ไม่มี unsafe-*, nosniff", scn="n_short", custom=custom_page_csp)

    # 3.5 concurrency
    for n in (2, 3, 5):
        for suffix in ("", "-b"):
            add(f"QA1-K-{n}{suffix}", "3.5", f"{n} คำขอพร้อมกัน", "Backend",
                "ทุกคำขอ completed ข้อความตรงของตัวเองทุกตัวอักษร ไม่มีข้อความข้ามคำขอ", scn="k_one", custom=custom_concurrency(n))

    # regression
    for v, title in (("ok", "POST /api/chat ปกติ"), ("thai", "POST /api/chat ภาษาไทย"), ("empty", "POST /api/chat ข้อความว่าง"), ("provider_error", "POST /api/chat เมื่อ Ollama ตอบ 500")):
        add(f"QA1-R-{v}", "regression", title, "Backend", "พฤติกรรมเดิมของ /api/chat ไม่เปลี่ยน", scn="n_short", custom=custom_regression(v))
    return cases


BLOCKED = [
    ("QA1-U-stop", "3.3", "ปุ่ม Stop Generating", "UI", "FE-002"),
    ("QA1-U-close-tab", "3.3", "ปิดแท็บระหว่างสตรีม", "UI", "FE-002"),
    ("QA1-U-navigate", "3.3", "เปลี่ยนหน้าระหว่างสตรีม", "UI", "FE-002"),
    ("QA1-U-render-html", "3.4", "คำตอบที่มี HTML/script แสดงเป็นข้อความในหน้าแชต", "UI", "FE-002"),
    ("QA1-U-queue-indicator", "3.5", "UI แสดงสถานะรอคิว", "UI", "FE-002"),
    ("QA1-U-partial-display", "3.2", "UI แสดงข้อความบางส่วนก่อน error", "UI", "FE-002"),
]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--output", default=str(ROOT / "docs" / "qa" / "qa-001-evidence.json"))
    ap.add_argument("--repeats", type=int, default=REPEATS)
    ap.add_argument("--only", help="run only cases whose id contains this text")
    ap.add_argument("--export-cases", metavar="PATH", help="write the case table (ids, Expected, owner) as JSON and exit")
    args = ap.parse_args(argv)
    if args.export_cases:
        table = [{k: c[k] for k in ("id", "group", "title", "owner", "expected")} for c in build_cases()]
        blocked = [{"id": i, "group": g, "title": t, "owner": o, "waiting_for": w} for i, g, t, o, w in BLOCKED]
        Path(args.export_cases).write_text(json.dumps({"repeats": args.repeats, "cases": table, "blocked": blocked}, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{len(table)} cases and {len(blocked)} blocked cases written to {args.export_cases}")
        return 0
    if not DLL.exists():
        print(f"Build the Backend first (dotnet build BC-LocalAI.slnx): {DLL} not found", file=sys.stderr)
        return 2

    log_dir = Path(os.environ.get("TEMP", ".")) / "qa001-logs"
    log_dir.mkdir(parents=True, exist_ok=True)

    FakeState_ = FakeState()
    Handler.state = FakeState_

    class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
        daemon_threads = True
        allow_reuse_address = True

        def handle_error(self, request, client_address):
            pass  # clients that disconnect on purpose are part of the scenarios

    server = Server(("127.0.0.1", 0), Handler)
    fake_port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    closed = socket.socket()
    closed.bind(("127.0.0.1", 0))
    dead_port = closed.getsockname()[1]
    closed.close()

    base = 18200 + (os.getpid() % 500)
    backends = {}
    try:
        backends["main"] = Backend(base, fake_port, "main", timeout=25, log_dir=log_dir)
        backends["short"] = Backend(base + 1, fake_port, "short", timeout=3, log_dir=log_dir)
        backends["bytes"] = Backend(base + 2, fake_port, "bytes", timeout=25, max_bytes=4096, log_dir=log_dir)
        backends["main_down"] = Backend(base + 3, dead_port, "down", timeout=10, log_dir=log_dir)
        started = time.time()
        cases = [c for c in build_cases() if not args.only or args.only in c["id"]]
        results = []
        for case in cases:
            runs = []
            for _ in range(args.repeats):
                try:
                    runs.append(run_case(case, backends, FakeState_))
                except Exception as exc:  # a harness failure is a failed case, never a silent pass
                    runs.append({"passed": False, "problems": [f"harness error {type(exc).__name__}: {exc}"], "actual": {}, "elapsed_s": 0})
            passed = all(r["passed"] for r in runs)
            results.append({"id": case["id"], "group": case["group"], "title": case["title"], "owner": case["owner"],
                            "expected": case["expected"], "runs": runs, "passed": passed})
            print(("PASS " if passed else "FAIL ") + case["id"] + ("" if passed else "  " + "; ".join(runs[0]["problems"] or runs[-1]["problems"])[:200]))
        # Backend logs must never contain a prompt or answer (acceptance: no user text in logs).
        leaks = []
        for b in backends.values():
            b.log.flush()
            text = Path(b.log_path).read_text(encoding="utf-8", errors="replace")
            for canary in (CANARY_PROMPT, CANARY_ANSWER, "ci-fake-value-not-real", "secret chain of thought", "ความคิดลับ"):
                if canary in text:
                    leaks.append(f"{b.name}: {canary}")
        results.append({"id": "QA1-L-log-privacy", "group": "3.4", "title": "log ของ Backend ไม่มีคำถาม คำตอบ หรือรายละเอียดภายในของ Ollama",
                        "owner": "Backend", "expected": "ไม่พบ canary ของ prompt/answer/ข้อความ error ภายในใน log ทุก instance",
                        "runs": [{"passed": not leaks, "problems": leaks, "actual": {"logs_checked": [b.name for b in backends.values()]}, "elapsed_s": 0}],
                        "passed": not leaks})
        print(("PASS " if not leaks else "FAIL ") + "QA1-L-log-privacy " + "; ".join(leaks))
    finally:
        for b in backends.values():
            b.stop()
        server.shutdown()

    total_runs = sum(len(r["runs"]) for r in results)
    out = {
        "generated_utc": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()),
        "repeats": args.repeats, "cases": len(results), "case_runs": total_runs,
        "passed_cases": sum(1 for r in results if r["passed"]),
        "failed_cases": [r["id"] for r in results if not r["passed"]],
        "blocked": [{"id": i, "group": g, "title": t, "owner": o, "waiting_for": w} for i, g, t, o, w in BLOCKED],
        "duration_s": round(time.time() - started, 1),
        "results": results,
    }
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n{out['passed_cases']}/{out['cases']} cases passed ({total_runs} runs), {len(BLOCKED)} blocked on FE-002. Evidence: {args.output}")
    return 0 if not out["failed_cases"] else 1


if __name__ == "__main__":
    sys.exit(main())
