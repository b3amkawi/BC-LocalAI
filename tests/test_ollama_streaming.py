"""AI-005: streaming reader and test script against a fake Ollama (no model needed).

The fake server replays the behaviours observed on a real qwen3:4b (see
docs/ai/ollama-streaming-contract.md): token-sized NDJSON records, escapes and key names split
across records, a separate `thinking` field, an `{"error": ...}` record in the middle of a stream,
`done_reason` length/load, and a model runner that stays busy until the client connection closes.

Run from the repository root: python -m unittest discover -s tests -v
Requires Python 3 and PowerShell 7 (pwsh) on PATH, for development tests only.
"""

import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / "scripts" / "OllamaStreaming.psm1"
SCRIPT = ROOT / "scripts" / "test-ollama-streaming.ps1"
PWSH = shutil.which("pwsh")
MODEL = "qwen3:4b"


def record(response="", done=False, **extra):
    return {"model": MODEL, "created_at": "2026-10-20T00:00:00Z", "response": response, "done": done, **extra}


def final(reason="stop", **extra):
    return record("", True, done_reason=reason, context=[1, 2, 3], total_duration=1_000_000, load_duration=100_000,
                  prompt_eval_count=10, prompt_eval_duration=100_000, eval_count=5, eval_duration=200_000, **extra)


def line(obj):
    return (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")


def answer_records(text, piece=3):
    """The answer JSON cut into small pieces the way Ollama streams it (escapes may be split)."""
    raw = json.dumps({"answer": text}, ensure_ascii=False)
    return [record(raw[i:i + piece]) for i in range(0, len(raw), piece)]


class FakeOllama:
    """Fake Ollama. `behaviour(body)` returns a list of steps for /api/generate:
    ("record", dict) one NDJSON line, ("bytes", b) raw bytes as one HTTP chunk, ("sleep", s),
    ("end",) terminate normally, ("abort",) reset the connection, ("status", code, bytes, content_type)."""

    def __init__(self, behaviour):
        self.behaviour = behaviour
        self.requests = []
        self.client_gone_after = []      # seconds from the first byte until a write failed
        self.generate_lock = threading.Lock()   # NUM_PARALLEL=1: one generation at a time
        self.linger_after_disconnect = 0.0      # > 0 models a runner that keeps working after the client left
        outer = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_):
                pass

            def send_plain(self, status, payload, content_type="application/json"):
                data = payload if isinstance(payload, bytes) else json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                if self.path == "/api/version":
                    self.send_plain(200, {"version": "fake-1.0"})
                elif self.path == "/api/tags":
                    self.send_plain(200, {"models": [{"name": MODEL, "digest": "f" * 64}]})
                elif self.path == "/api/ps":
                    self.send_plain(200, {"models": []})
                else:
                    self.send_plain(404, b"404 page not found", "text/plain")

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                raw = self.rfile.read(length)
                try:
                    body = json.loads(raw.decode("utf-8"))
                except Exception:
                    body = None
                outer.requests.append(body)
                if self.path != "/api/generate":
                    return self.send_plain(404, b"404 page not found", "text/plain")
                if body is None:
                    return self.send_plain(400, {"error": "unexpected EOF"})
                steps = outer.behaviour(body)
                with outer.generate_lock:
                    self.stream(steps)

            def chunk(self, data):
                self.wfile.write(f"{len(data):X}\r\n".encode() + data + b"\r\n")
                self.wfile.flush()

            def stream(self, steps):
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                started = False
                t0 = time.perf_counter()
                try:
                    for step in steps:
                        kind = step[0]
                        if kind == "status":
                            return self.send_plain(step[1], step[2], step[3])
                        if not started:
                            self.send_response(200)
                            self.send_header("Content-Type", "application/x-ndjson")
                            self.send_header("Transfer-Encoding", "chunked")
                            self.end_headers()
                            started = True
                        if kind == "record":
                            self.chunk(line(step[1]))
                        elif kind == "bytes":
                            self.chunk(step[1])
                        elif kind == "sleep":
                            time.sleep(step[1])
                        elif kind == "end":
                            self.wfile.write(b"0\r\n\r\n")
                            self.wfile.flush()
                        elif kind == "abort":
                            self.connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                            self.connection.close()
                            self.close_connection = True
                            return
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
                    outer.client_gone_after.append(time.perf_counter() - t0)
                    time.sleep(outer.linger_after_disconnect)   # still holds generate_lock
                    self.close_connection = True
                    return
                self.close_connection = True

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_port}"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


BODY = {"model": MODEL, "prompt": "hi", "stream": True, "think": False}


def reference_behaviour(mode="normal"):
    """A conformant fake of the scenarios in test-ollama-streaming.ps1. `mode` breaks one behaviour."""

    def answer_for(prompt):
        if "😊" in prompt:
            return "Hello 😊🎉 ครับ"
        if '"hi"' in prompt:
            return 'He said "hi" twice'
        if "line break" in prompt:
            return "A\nB"
        if "ประเทศไทย" in prompt:
            return "กรุงเทพมหานคร"
        if "hotosynthesis" in prompt:
            return "Photosynthesis is the process by which green plants convert light energy into chemical energy."
        if "Sun" in prompt:
            return "The Sun, a massive star at the center of our solar system, provides light and heat essential for life."
        if "France" in prompt:
            return "Paris"
        if "Japan" in prompt:
            return "Tokyo"
        return "Hi"

    def behaviour(body):
        prompt = body.get("prompt", "")
        options = body.get("options") or {}
        if body.get("model") != MODEL:
            return [("status", 404, json.dumps({"error": f"model '{body.get('model')}' not found"}).encode(), "application/json")]
        if not isinstance(options.get("num_predict"), int):
            return [("status", 500, json.dumps({"error": 'option "num_predict" must be of type integer'}).encode(), "application/json")]
        if prompt == "":
            return [("status", 200, json.dumps(record("", True, done_reason="load")).encode(), "application/json")]
        limit = options["num_predict"]
        steps = []
        if "long essay" in prompt:
            steps.append(("sleep", 0.05))
            for i in range(min(limit, 1024)):
                steps.append(("record", record("", False, thinking=f"word{i} ")))
                steps.append(("sleep", 0.02))
            steps.append(("record", final("length")))
            steps.append(("end",))
            return steps
        if body.get("format") is None:
            for token in ["Okay", ",", " the", " user", " asks", " for", " the", " capital"]:
                steps.append(("record", record(token)))
            steps.append(("record", final("length")))
            steps.append(("end",))
            return steps
        if body.get("think") is True:
            for i in range(30):
                steps.append(("record", record("", False, thinking=f"thinking {i} ")))
                steps.append(("sleep", 0.01))
        answer = answer_for(prompt)
        if mode == "leaky_think":
            answer = "<think>" + answer
        tokens = answer_records(answer)
        if limit <= 8:
            tokens = tokens[:limit]
            steps += [("record", t) for t in tokens] + [("record", final("length")), ("end",)]
            return steps
        for token in tokens:
            steps.append(("record", token))
            steps.append(("sleep", 0.01))
        steps += [("record", final("stop")), ("end",)]
        return steps

    return behaviour


def run_pwsh(command, timeout=120):
    env = {**os.environ, "NO_COLOR": "1"}
    return subprocess.run([PWSH, "-NoProfile", "-Command", command], capture_output=True, encoding="utf-8",
                          errors="replace", timeout=timeout, env=env)


def stream_json(url, body=None, extra="", import_extra=""):
    """Runs Invoke-OllamaStream in pwsh and returns the parsed result."""
    body_json = json.dumps(body if body is not None else BODY, ensure_ascii=True).replace("'", "''")
    command = (
        f"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); Import-Module '{MODULE}' -Force; {import_extra} "
        f"$b = '{body_json}' | ConvertFrom-Json -AsHashtable; "
        f"$r = Invoke-OllamaStream -BaseUrl '{url}' -Body $b {extra}; "
        f"$r | ConvertTo-Json -Depth 8 -Compress"
    )
    completed = run_pwsh(command)
    if completed.returncode != 0 or not completed.stdout.strip():
        raise AssertionError(f"pwsh failed ({completed.returncode}): {completed.stderr}\n{completed.stdout}")
    return json.loads(completed.stdout)


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class DecoderTests(unittest.TestCase):
    def decode(self, cases):
        """cases: {name: [fragments]} -> {name: {Answer, Complete, Error, Deltas}} (one pwsh call)."""
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "cases.json"
            path.write_text(json.dumps(cases), encoding="utf-8")
            command = (
                "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); "
                f"Import-Module '{MODULE}' -Force; "
                f"$cases = Get-Content -Raw -LiteralPath '{path}' -Encoding utf8 | ConvertFrom-Json -AsHashtable; "
                "$out = [ordered]@{}; foreach ($k in $cases.Keys) { $out[$k] = Convert-AnswerFragments -Fragments ([string[]]$cases[$k]) }; "
                "$out | ConvertTo-Json -Depth 6 -Compress"
            )
            completed = run_pwsh(command)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return json.loads(completed.stdout)

    def test_real_fragments_and_valid_variants(self):
        # The first two are fragment lists captured from a real qwen3:4b stream.
        json_in_json = ["{\n", " ", " \"", "a", "ns", "we", "r", "\":", " \"", "Set", " answer", " to", " exactly", " this",
                        " JSON", " text", ":", " {", "\\\"", "a", "\\\":", "1", ",\\\"", "b", "\\", "\":[", "2", ",", "3", "]}\"\n", "}"]
        thai = ["{\"", "answer", "\":", " \"", "กร", "ุง", "เทพ", "ม", "ห", "าน", "คร", "\"}", ""]
        results = self.decode({
            "json_in_json": json_in_json,
            "thai": thai,
            "surrogate_pair_split": ["{\"answer\":\"x\\ud8", "3d\\ude0a y\"}"],
            "all_escapes": ["{\"answer\": \"a\\n\\t\\\\\\/\\b\\f\\r\\\"z\"}"],
            "whitespace": ["  {\n  \"answer\"  :\n  \"A\"\n}\n"],
            "empty_fragments": ["", "{", "", "\"answer\":\"ok\"", "", "}", ""],
            "one_char_at_a_time": list("{\"answer\": \"ไทย 😊\"}"),
        })
        self.assertEqual(results["json_in_json"]["Answer"], 'Set answer to exactly this JSON text: {"a":1,"b":[2,3]}')
        self.assertEqual(results["thai"]["Answer"], "กรุงเทพมหานคร")
        self.assertEqual(results["surrogate_pair_split"]["Answer"], "x😊 y")
        self.assertEqual(results["all_escapes"]["Answer"], 'a\n\t\\/\b\f\r"z')
        self.assertEqual(results["whitespace"]["Answer"], "A")
        self.assertEqual(results["empty_fragments"]["Answer"], "ok")
        self.assertEqual(results["one_char_at_a_time"]["Answer"], "ไทย 😊")
        for name, result in results.items():
            with self.subTest(name=name):
                self.assertTrue(result["Complete"])
                self.assertIsNone(result["Error"])
                self.assertEqual("".join(result["Deltas"]), result["Answer"])

    def test_only_the_answer_value_is_emitted_incrementally(self):
        result = self.decode({"x": ["{\"ans", "wer\": \"Hel", "lo wor", "ld\"}"]})["x"]
        self.assertEqual(result["Deltas"], ["", "Hel", "lo wor", "ld"])

    def test_malformed_answers_are_rejected_with_a_reason(self):
        cases = {
            "prose": ["Okay, the user asks"],
            "wrong_key": ["{\"foo\": \"x\"}"],
            "extra_field": ["{\"answer\": \"a\", \"b\": 1}"],
            "duplicate_answer": ["{\"answer\": \"a\", \"answer\": \"b\"}"],
            "not_a_string": ["{\"answer\": 1}"],
            "invalid_escape": ["{\"answer\": \"a\\xb\"}"],
            "raw_newline": ["{\"answer\": \"a\nb\"}"],
            "trailing": ["{\"answer\": \"a\"} extra"],
            "empty_object": ["{}"],
            "bad_hex": ["{\"answer\": \"\\u12G4\"}"],
        }
        expected = {"prose": "not_a_json_object", "wrong_key": "unexpected_field", "extra_field": "unexpected_field",
                    "duplicate_answer": "unexpected_field", "not_a_string": "answer_not_string", "invalid_escape": "invalid_escape",
                    "raw_newline": "invalid_string_character", "trailing": "trailing_data", "empty_object": "missing_answer",
                    "bad_hex": "invalid_escape"}
        results = self.decode(cases)
        for name, reason in expected.items():
            with self.subTest(name=name):
                self.assertEqual(results[name]["Error"], reason)

    def test_incomplete_answer_is_not_complete(self):
        result = self.decode({"x": ["{\"answer\": \"abc"]})["x"]
        self.assertFalse(result["Complete"])
        self.assertIsNone(result["Error"])
        self.assertEqual(result["Answer"], "abc")


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class StreamReaderTests(unittest.TestCase):
    def run_case(self, steps, extra="", body=None):
        with FakeOllama(lambda _body: steps) as server:
            result = stream_json(server.url, body, extra)
        return result, server

    def test_completed_with_lines_and_utf8_split_across_network_reads(self):
        text = "สวัสดี 😊"
        raw = b"".join(line(r) for r in answer_records(text, piece=2)) + line(final())
        # cut at arbitrary byte positions, including inside a 3-byte Thai character and a 4-byte emoji
        cuts = [7, 61, 140, len(raw) // 2 + 1, len(raw) // 2 + 2, len(raw) - 30]
        pieces, previous = [], 0
        for cut in sorted(set(cuts)):
            pieces.append(raw[previous:cut])
            previous = cut
        pieces.append(raw[previous:])
        steps = []
        for piece in pieces:
            steps += [("bytes", piece), ("sleep", 0.05)]
        steps.append(("end",))
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "completed", result["problems"])
        self.assertEqual(result["answer"], text)
        self.assertNotIn("\ufffd", result["answer"])
        self.assertEqual(result["done_reason"], "stop")
        self.assertIsNone(result["error_code"])

    def test_thinking_field_is_counted_and_never_forwarded(self):
        steps = [("record", record("", False, thinking="Okay, so ")), ("record", record("", False, thinking="Paris."))]
        steps += [("record", r) for r in answer_records("Paris")] + [("record", final()), ("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "completed")
        self.assertTrue(result["has_thinking_field"])
        self.assertEqual(result["thinking_chars"], len("Okay, so ") + len("Paris."))
        self.assertEqual(result["answer"], "Paris")
        self.assertNotIn("Okay", result["answer"])

    def test_unknown_record_fields_are_tolerated(self):
        steps = [("record", {**r, "extra_field": 1, "nested": {"a": [1, 2]}}) for r in answer_records("ok")]
        steps += [("record", final()), ("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "completed", result["problems"])

    def test_metrics_of_the_final_record_are_read(self):
        steps = [("record", r) for r in answer_records("ok")] + [("record", final()), ("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual((result["eval_count"], result["prompt_eval_count"]), (5, 10))
        self.assertGreater(result["records"], 3)

    def test_stream_that_ends_before_done_is_interrupted(self):
        steps = [("record", r) for r in answer_records("hello")[:3]] + [("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "interrupted")
        self.assertEqual(result["error_code"], "AI_INVALID_RESPONSE")
        self.assertFalse(result["done"])

    def test_connection_reset_is_interrupted(self):
        steps = [("record", r) for r in answer_records("hello")[:3]] + [("sleep", 0.05), ("abort",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "interrupted")
        self.assertEqual(result["error_code"], "AI_INVALID_RESPONSE")

    def test_error_record_is_provider_error_with_a_sanitized_message(self):
        message = "an error was encountered while running the model: read tcp 127.0.0.1:63012-\u003e127.0.0.1:64649: wsarecv: forcibly closed"
        steps = [("record", r) for r in answer_records("hello")[:3]] + [("record", {"error": message}), ("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "provider_error")
        self.assertEqual(result["error_code"], "AI_PROVIDER_ERROR")
        self.assertFalse(result["done"])
        self.assertNotIn("127.0.0.1:63012", result["upstream_error"])
        self.assertNotRegex(result["upstream_error"], r"127\.0\.0\.1:\d")
        self.assertIn("<loopback>", result["upstream_error"])

    def test_invalid_streams_are_classified_invalid(self):
        good = [("record", r) for r in answer_records("ok")]
        cases = {
            "malformed JSON line": [("bytes", b"{not json}\n"), ("end",)],
            "line that is not an object": [("bytes", b"[1,2]\n"), ("end",)],
            "wrong model": [("record", {**record("{"), "model": "other:1b"}), ("end",)],
            "done is not boolean": [("bytes", b'{"model": "qwen3:4b", "response": "", "done": "yes"}\n'), ("end",)],
            "response is not a string": [("bytes", b'{"model": "qwen3:4b", "response": 5, "done": false}\n'), ("end",)],
            "done_reason load": good + [("record", final("load")), ("end",)],
            "done_reason unknown": good + [("record", final("weird")), ("end",)],
            "plain text answer": [("record", record("Okay")), ("record", final()), ("end",)],
            "incomplete answer json": [("record", record('{"answer": "ab')), ("record", final()), ("end",)],
            "empty answer": [("record", r) for r in answer_records("")] + [("record", final()), ("end",)],
            "thinking marker in answer": [("record", r) for r in answer_records("<think>x")] + [("record", final()), ("end",)],
            "data after done": good + [("record", final()), ("record", record("late")), ("end",)],
            "extra answer field": [("record", record('{"answer": "a", "b": 1}')), ("record", final()), ("end",)],
        }
        for name, steps in cases.items():
            with self.subTest(name=name):
                result, _ = self.run_case(steps)
                self.assertEqual(result["outcome"], "invalid", result)
                self.assertEqual(result["error_code"], "AI_INVALID_RESPONSE")

    def test_length_is_truncated_not_completed(self):
        steps = [("record", r) for r in answer_records("a long answer that was cut")[:4]] + [("record", final("length")), ("end",)]
        result, _ = self.run_case(steps)
        self.assertEqual(result["outcome"], "truncated")
        self.assertEqual(result["error_code"], "AI_RESPONSE_TOO_LARGE")
        self.assertEqual(result["done_reason"], "length")

    def test_http_errors_before_the_stream_starts(self):
        for status, payload, ctype in ((404, b'{"error":"model not found"}', "application/json"),
                                       (500, b'{"error":"option must be integer"}', "application/json"),
                                       (400, b'{"error":"unexpected EOF"}', "application/json"),
                                       (404, b"404 page not found", "text/plain"),
                                       (503, b"", "text/plain")):
            with self.subTest(status=status, content_type=ctype):
                result, _ = self.run_case([("status", status, payload, ctype)])
                self.assertEqual(result["outcome"], "http_error")
                self.assertEqual(result["http_status"], status)
                self.assertEqual(result["error_code"], "AI_PROVIDER_ERROR")

    def test_unavailable_endpoint(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        result = stream_json(f"http://127.0.0.1:{port}")
        self.assertEqual(result["outcome"], "unavailable")
        self.assertEqual(result["error_code"], "AI_UNAVAILABLE")

    def test_idle_timeout_when_the_stream_stalls(self):
        steps = [("record", record('{"answer": "'))] + [("sleep", 5), ("end",)]
        result, _ = self.run_case(steps, "-IdleTimeoutSec 1")
        self.assertEqual(result["outcome"], "timeout")
        self.assertEqual(result["timeout_kind"], "idle")
        self.assertEqual(result["error_code"], "AI_TIMEOUT")
        self.assertLess(result["total_ms"], 3500)

    def test_total_timeout_stops_a_slow_but_steady_stream(self):
        steps = [("record", record('{"answer": "'))]
        for _ in range(100):
            steps += [("record", record("a")), ("sleep", 0.1)]
        with FakeOllama(lambda _b: steps) as server:
            result = stream_json(server.url, None, "-TotalTimeoutSec 1 -IdleTimeoutSec 30")
            time.sleep(0.6)
            gone = list(server.client_gone_after)
        self.assertEqual(result["outcome"], "timeout")
        self.assertEqual(result["timeout_kind"], "total")
        self.assertTrue(gone, "the server must notice that the client closed the connection")
        self.assertLess(gone[0], 2.5)

    def test_cancellation_closes_the_connection_at_once_but_default_drain_does_not(self):
        """Cancel, then immediately send the next request in the SAME process (a process exit would kill the
        background drain). The fake runner is busy until the client connection closes, like Ollama."""
        schema = {"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"], "additionalProperties": False}
        body = {"model": MODEL, "prompt": "", "stream": True, "think": False, "format": schema, "options": {"temperature": 0, "num_predict": 1024}}
        long_body = json.dumps({**body, "prompt": "Write a long essay", "think": True}).replace("'", "''")
        short_body = json.dumps({**body, "prompt": "Say hi.", "options": {"temperature": 0, "num_predict": 64}}).replace("'", "''")
        with FakeOllama(reference_behaviour()) as server:
            command = (
                f"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); Import-Module '{MODULE}' -Force; "
                f"$long = '{long_body}' | ConvertFrom-Json -AsHashtable; $short = '{short_body}' | ConvertFrom-Json -AsHashtable; "
                f"$u = '{server.url}'; $out = [ordered]@{{}}; "
                "$c = Invoke-OllamaStream -BaseUrl $u -Body $long -CancelAfterRecords 5; "
                "$out.closed_cancel = $c.outcome; $out.closed_records = $c.records; "
                "$out.closed_next_first_record_ms = (Invoke-OllamaStream -BaseUrl $u -Body $short).first_record_ms; "
                "$c = Invoke-OllamaStream -BaseUrl $u -Body $long -CancelAfterRecords 5 -DrainOnDispose; "
                "$out.drained_cancel = $c.outcome; "
                "$out.drained_next_first_record_ms = (Invoke-OllamaStream -BaseUrl $u -Body $short).first_record_ms; "
                "$out | ConvertTo-Json -Compress"
            )
            completed = run_pwsh(command)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        out = json.loads(completed.stdout)
        self.assertEqual((out["closed_cancel"], out["closed_records"], out["drained_cancel"]), ("cancelled", 5, "cancelled"))
        self.assertLess(out["closed_next_first_record_ms"], 1000, "the connection must be closed at once")
        self.assertGreater(out["drained_next_first_record_ms"], 1500, "the .NET default keeps the connection open for about 2 s")

    def test_on_record_hook_receives_the_running_record_count(self):
        steps = [("record", r) for r in answer_records("hello")] + [("record", final()), ("end",)]
        with FakeOllama(lambda _b: steps) as server:
            result = stream_json(server.url, None, "-OnRecord { param($n) $script:seen = $n }")
        self.assertEqual(result["outcome"], "completed")

    def test_only_loopback_endpoints_are_allowed(self):
        completed = run_pwsh(f"Import-Module '{MODULE}' -Force; Invoke-OllamaStream -BaseUrl 'http://192.168.1.10:11434' -Body @{{ a = 1 }}")
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("loopback", completed.stderr + completed.stdout)

    def test_public_error_code_mapping(self):
        command = (f"Import-Module '{MODULE}' -Force; "
                   "$o = 'completed','cancelled','unavailable','timeout','http_error','provider_error','truncated','invalid','interrupted'; "
                   "($o | ForEach-Object { \"$_=\" + (Get-PublicErrorCode $_) }) -join ';'")
        completed = run_pwsh(command)
        mapping = dict(part.split("=") for part in completed.stdout.strip().split(";"))
        self.assertEqual(mapping, {"completed": "", "cancelled": "", "unavailable": "AI_UNAVAILABLE", "timeout": "AI_TIMEOUT",
                                   "http_error": "AI_PROVIDER_ERROR", "provider_error": "AI_PROVIDER_ERROR",
                                   "truncated": "AI_RESPONSE_TOO_LARGE", "invalid": "AI_INVALID_RESPONSE",
                                   "interrupted": "AI_INVALID_RESPONSE"})


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class TestScriptAgainstFakeOllamaTests(unittest.TestCase):
    def run_script(self, behaviour, extra=(), linger=0.0):
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder) / "evidence.json"
            with FakeOllama(behaviour) as server:
                server.linger_after_disconnect = linger
                completed = subprocess.run(
                    [PWSH, "-NoProfile", "-File", str(SCRIPT), "-BaseUrl", server.url, "-OutputPath", str(evidence), *extra],
                    capture_output=True, encoding="utf-8", errors="replace", timeout=240)
            data = json.loads(evidence.read_text(encoding="utf-8")) if evidence.exists() else None
        return completed, data

    def test_conformant_server_passes_every_scenario(self):
        completed, data = self.run_script(reference_behaviour())
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
        self.assertEqual((data["total"], data["failed"]), (13, 0))
        self.assertEqual([s["id"] for s in data["scenarios"]], [f"S{n:02}" for n in range(1, 14)])
        self.assertTrue(all(check["passed"] for s in data["scenarios"] for check in s["checks"]))
        self.assertTrue(all(s["expected"] and s["checks"] for s in data["scenarios"]))
        self.assertEqual(data["ollama_version"], "fake-1.0")

    def test_a_thinking_marker_in_the_answer_fails_the_run(self):
        completed, data = self.run_script(reference_behaviour("leaky_think"))
        self.assertEqual(completed.returncode, 1, completed.stdout + completed.stderr)
        self.assertGreater(data["failed"], 0)
        self.assertIn("FAIL S01", completed.stdout)

    def test_a_server_that_keeps_generating_after_a_disconnect_fails_the_cancellation_scenario(self):
        completed, data = self.run_script(reference_behaviour(), linger=3.0)
        self.assertEqual(completed.returncode, 1, completed.stdout)
        failed = {s["id"] for s in data["scenarios"] if not s["passed"]}
        self.assertIn("S10", failed)

    def test_unreachable_ollama_fails_before_any_scenario(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder) / "evidence.json"
            completed = subprocess.run([PWSH, "-NoProfile", "-File", str(SCRIPT), "-BaseUrl", f"http://127.0.0.1:{port}",
                                        "-OutputPath", str(evidence)], capture_output=True, encoding="utf-8", errors="replace", timeout=60)
            self.assertFalse(evidence.exists())
        self.assertEqual(completed.returncode, 1)

    def test_unsafe_urls_fail_before_any_request(self):
        for base in ("http://192.168.1.10:11434", "http://user:pw@127.0.0.1:11434", "http://127.0.0.1:11434/x"):
            with self.subTest(base=base):
                completed = subprocess.run([PWSH, "-NoProfile", "-File", str(SCRIPT), "-BaseUrl", base],
                                           capture_output=True, encoding="utf-8", errors="replace", timeout=60)
                self.assertEqual(completed.returncode, 1)

    def test_bad_output_directory_fails_before_any_request(self):
        with FakeOllama(reference_behaviour()) as server:
            completed = subprocess.run([PWSH, "-NoProfile", "-File", str(SCRIPT), "-BaseUrl", server.url,
                                        "-OutputPath", str(ROOT / "no-such-folder" / "e.json")],
                                       capture_output=True, encoding="utf-8", errors="replace", timeout=60)
            self.assertEqual(completed.returncode, 1)
            self.assertFalse(server.requests)


if __name__ == "__main__":
    unittest.main()
