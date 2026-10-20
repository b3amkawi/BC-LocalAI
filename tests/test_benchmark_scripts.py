"""AI-004 benchmark scripts against loopback mock Ollama/Backend servers (no model needed).

Run from the repository root: python -m unittest discover -s tests -v
Requires Python 3 and PowerShell 7 (pwsh) on PATH, for development tests only.
"""

import csv
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


ROOT = Path(__file__).resolve().parents[1]
OLLAMA_SCRIPT = ROOT / "scripts" / "benchmark" / "ollama-benchmark.ps1"
BACKEND_SCRIPT = ROOT / "scripts" / "benchmark" / "backend-benchmark.ps1"
CONTRACT = ROOT / "BC-LocalAI" / "Models" / "Ollama" / "OllamaGenerateRequest.cs"
PWSH = shutil.which("pwsh")


def ollama_ok(_request):
    return {"body": {
        "model": "qwen3:4b", "done": True, "done_reason": "stop",
        "response": json.dumps({"answer": "ตกลง"}, ensure_ascii=False),
        "total_duration": 200_000_000, "load_duration": 1_000_000,
        "prompt_eval_count": 40, "prompt_eval_duration": 20_000_000,
        "eval_count": 10, "eval_duration": 100_000_000,
    }}


def backend_ok(_request):
    return {"body": {"response": "ตกลง", "model": "qwen3:4b"}}


class MockServer:
    """Threaded loopback server that records requests and the peak number in flight."""

    def __init__(self, reply, tags=None):
        self.requests = []
        self.in_flight = 0
        self.peak = 0
        lock = threading.Lock()
        outer = self
        tags = {"models": [{"name": "qwen3:4b", "digest": "abc123", "details": {"quantization_level": "Q4_K_M"}}]} \
            if tags is None else tags

        class Handler(BaseHTTPRequestHandler):
            def send_json(self, status, payload):
                data = payload.encode("utf-8") if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass

            def do_GET(self):
                outer.requests.append((self.path, None))
                if self.path == "/api/version":
                    self.send_json(200, {"version": "mock-1.0"})
                elif self.path == "/api/tags":
                    self.send_json(200, tags)
                else:
                    self.send_json(404, {"error": "not found"})

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length).decode("utf-8"))
                with lock:
                    outer.requests.append((self.path, body))
                    outer.in_flight += 1
                    outer.peak = max(outer.peak, outer.in_flight)
                try:
                    result = reply(body)
                    time.sleep(result.get("delay", 0.15))
                    self.send_json(result.get("status", 200), result.get("raw", result.get("body", {})))
                finally:
                    with lock:
                        outer.in_flight -= 1

            def log_message(self, *_):
                pass

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

    def posts(self):
        return [body for path, body in self.requests if body is not None]


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class BenchmarkScriptTests(unittest.TestCase):
    def run_script(self, script, base_url, *args, timeout=120):
        folder = tempfile.TemporaryDirectory(prefix="bench-")
        self.addCleanup(folder.cleanup)
        csv_path = Path(folder.name) / "results.csv"
        json_path = Path(folder.name) / "summary.json"
        command = [PWSH, "-NoProfile", "-File", str(script), "-BaseUrl", base_url,
                   "-OutputCsv", str(csv_path), "-OutputJson", str(json_path), *args]
        result = subprocess.run(command, capture_output=True, encoding="utf-8", errors="replace", timeout=timeout)
        rows = list(csv.DictReader(csv_path.open(encoding="utf-8"))) if csv_path.exists() else None
        summary = json.loads(json_path.read_text(encoding="utf-8")) if json_path.exists() else None
        return result, rows, summary

    def test_ollama_run_writes_rows_scenarios_and_fires_requests_concurrently(self):
        with MockServer(ollama_ok) as server:
            result, rows, summary = self.run_script(
                OLLAMA_SCRIPT, server.url, "-Concurrency", "1,5", "-Profiles", "short,long",
                "-Rounds", "2", "-WarmupRequests", "1", "-ResourceIntervalMs", "0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        measured = [row for row in rows if row["phase"] == "measured"]
        self.assertEqual(len(measured), 2 * 2 * (1 + 5))
        self.assertEqual(len([row for row in rows if row["phase"] == "warmup"]), 2)
        self.assertEqual(server.peak, 5)
        self.assertEqual([(s["profile"], s["concurrency"]) for s in summary["scenarios"]],
                         [("short", 1), ("short", 5), ("long", 1), ("long", 5)])
        scenario = summary["scenarios"][1]
        self.assertEqual(scenario["requests"], 10)
        self.assertEqual(scenario["success"], 10)
        self.assertEqual(scenario["success_rate"], 1)
        self.assertEqual(scenario["timeout_rate"], 0)
        self.assertFalse(scenario["p99_reliable"])
        self.assertGreater(scenario["throughput_rps"], 0)
        self.assertEqual(scenario["generation_tokens_per_sec_mean"], 100)
        row = measured[0]
        self.assertEqual(row["outcome"], "success")
        self.assertEqual(row["eval_count"], "10")
        self.assertEqual(row["total_ms"], "200")
        # queue wait = elapsed - (load 1 + prompt_eval 20 + eval 100) ms of Ollama work, not elapsed - total_duration
        self.assertAlmostEqual(float(row["queue_wait_ms"]), float(row["elapsed_ms"]) - 121, delta=0.2)
        self.assertLess(row["start_utc"], row["end_utc"])
        self.assertRegex(row["run_id"], r"^20\d{6}T\d{6}-ollama$")
        self.assertTrue(row["start_utc"].startswith("20"))
        self.assertTrue(all(path in ("/api/version", "/api/tags", "/api/generate") for path, _ in server.requests))
        self.assertEqual(summary["environment"]["ollama_version"], "mock-1.0")
        self.assertEqual(summary["environment"]["model_digest"], "abc123")
        self.assertTrue(summary["preliminary"])

    def test_ollama_request_body_matches_the_backend_contract(self):
        source = CONTRACT.read_text(encoding="utf-8")
        system_prompt = re.search(r'SystemPrompt =\s*"([^"]+)"', source)[1]
        num_predict = int(re.search(r'NumPredict \{ get; init; \} = (\d+);', source)[1])
        with MockServer(ollama_ok) as server:
            result, _, _ = self.run_script(OLLAMA_SCRIPT, server.url, "-Concurrency", "1", "-Profiles", "short",
                                           "-Rounds", "1", "-WarmupRequests", "0", "-ResourceIntervalMs", "0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = server.posts()[0]
        self.assertEqual(body["model"], "qwen3:4b")
        self.assertEqual(body["system"], system_prompt)
        self.assertEqual(body["options"], {"temperature": 0, "num_predict": num_predict})
        self.assertIs(body["think"], False)
        self.assertIs(body["stream"], False)
        self.assertEqual(body["format"]["required"], ["answer"])

    def test_ollama_outcomes_are_classified_and_kept_as_data(self):
        def reply(body):
            prompt = body["prompt"]
            if "capital of Japan" in prompt:
                return {"body": {**ollama_ok(body)["body"], "done_reason": "length"}}
            if "12 + 30" in prompt:
                return {"status": 500, "body": {"error": "model runner crashed"}}
            if "Red Planet" in prompt:
                return {"delay": 3}
            return ollama_ok(body)

        with MockServer(reply) as server:
            result, rows, summary = self.run_script(
                OLLAMA_SCRIPT, server.url, "-Concurrency", "5", "-Profiles", "short", "-Rounds", "1",
                "-WarmupRequests", "0", "-ResourceIntervalMs", "0", "-TimeoutSec", "1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outcomes = {row["prompt_id"]: row for row in rows if row["phase"] == "measured"}
        self.assertEqual(outcomes["short-1"]["outcome"], "truncated")
        self.assertEqual(outcomes["short-2"]["outcome"], "server_error")
        self.assertEqual(outcomes["short-2"]["error_code"], "OLLAMA_ERROR")
        self.assertEqual(outcomes["short-3"]["outcome"], "client_timeout")
        scenario = summary["scenarios"][0]
        self.assertEqual((scenario["success"], scenario["truncated"], scenario["server_error"], scenario["client_timeout"]),
                         (2, 1, 1, 1))
        self.assertEqual(scenario["timeout_rate"], 0.2)

    def test_backend_run_separates_server_timeout_from_server_error(self):
        def reply(body):
            if "capital of Japan" in body["message"]:
                return {"status": 504, "body": {"error": {"code": "AI_TIMEOUT", "message": "AI service timed out"}}}
            if "12 + 30" in body["message"]:
                return {"status": 502, "body": {"error": {"code": "AI_INVALID_RESPONSE", "message": "invalid"}}}
            return backend_ok(body)

        with MockServer(reply) as server:
            result, rows, summary = self.run_script(
                BACKEND_SCRIPT, server.url, "-Concurrency", "1 2", "-Profiles", "short", "-Rounds", "2",
                "-WarmupRequests", "0", "-ResourceIntervalMs", "0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(all(path == "/api/chat" for path, _ in server.requests))
        self.assertTrue(all(set(body) == {"message"} for body in server.posts()))
        by_outcome = {}
        for row in rows:
            by_outcome.setdefault(row["outcome"], set()).add(row["error_code"])
        self.assertEqual(by_outcome["server_timeout"], {"AI_TIMEOUT"})
        self.assertEqual(by_outcome["server_error"], {"AI_INVALID_RESPONSE"})
        self.assertEqual([s["concurrency"] for s in summary["scenarios"]], [1, 2])
        self.assertGreater(sum(s["server_timeout"] for s in summary["scenarios"]), 0)
        self.assertIn("backend_ollama_timeout_seconds", summary["environment"])

    def test_append_keeps_one_header_and_both_targets(self):
        folder = tempfile.TemporaryDirectory(prefix="bench-")
        self.addCleanup(folder.cleanup)
        csv_path = Path(folder.name) / "results.csv"
        common = ["-Concurrency", "1", "-Profiles", "short", "-Rounds", "1", "-WarmupRequests", "0", "-ResourceIntervalMs", "0",
                  "-OutputCsv", str(csv_path)]
        with MockServer(ollama_ok) as ollama, MockServer(backend_ok) as backend:
            first = subprocess.run([PWSH, "-NoProfile", "-File", str(OLLAMA_SCRIPT), "-BaseUrl", ollama.url, *common,
                                    "-OutputJson", str(Path(folder.name) / "o.json")], capture_output=True, encoding="utf-8", timeout=60)
            second = subprocess.run([PWSH, "-NoProfile", "-File", str(BACKEND_SCRIPT), "-BaseUrl", backend.url, *common,
                                     "-AppendCsv", "-OutputJson", str(Path(folder.name) / "b.json")], capture_output=True, encoding="utf-8", timeout=60)
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(second.returncode, 0, second.stderr)
        lines = csv_path.read_text(encoding="utf-8").splitlines()
        self.assertEqual(sum(line.startswith("run_id,") for line in lines), 1)
        self.assertEqual({row["target"] for row in csv.DictReader(lines)}, {"ollama", "backend"})

    def test_low_available_memory_stops_before_measuring(self):
        with MockServer(backend_ok) as server:
            result, rows, summary = self.run_script(
                BACKEND_SCRIPT, server.url, "-Concurrency", "1", "-Profiles", "short", "-Rounds", "1",
                "-WarmupRequests", "0", "-ResourceIntervalMs", "200", "-MinAvailableMemoryMB", "1048576")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("STOPPED", result.stderr)
        self.assertIn("available RAM", summary["aborted"])
        self.assertFalse([row for row in rows if row["phase"] == "measured"])
        self.assertTrue(summary["resource_samples"])

    def test_resource_samples_are_attached_to_scenarios(self):
        with MockServer(backend_ok) as server:
            result, _, summary = self.run_script(
                BACKEND_SCRIPT, server.url, "-Concurrency", "2", "-Profiles", "short", "-Rounds", "3",
                "-WarmupRequests", "0", "-ResourceIntervalMs", "200", "-MinAvailableMemoryMB", "0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        scenario = summary["scenarios"][0]
        self.assertGreater(scenario["resource_sample_count"], 0)
        self.assertIsNotNone(scenario["ram_available_mb_min"])
        self.assertIsNotNone(scenario["cpu_percent_max"])

    def test_invalid_input_fails_before_any_request(self):
        with MockServer(backend_ok) as server:
            cases = [
                (BACKEND_SCRIPT, "http://localhost:11434", []),
                (BACKEND_SCRIPT, "http://192.168.1.10:5290", []),
                (BACKEND_SCRIPT, "http://user:secret@localhost:5290", []),
                (BACKEND_SCRIPT, server.url, ["-Profiles", "huge"]),
                (BACKEND_SCRIPT, server.url, ["-Concurrency", "0"]),
                (BACKEND_SCRIPT, server.url, ["-Concurrency", "1,abc"]),
                (OLLAMA_SCRIPT, "http://10.0.0.5:11434", []),
            ]
            for script, url, extra in cases:
                with self.subTest(url=url, extra=extra):
                    result, rows, summary = self.run_script(script, url, *extra, "-ResourceIntervalMs", "0")
                    self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                    self.assertIsNone(rows)
                    self.assertIsNone(summary)
            self.assertFalse(server.requests)

    def test_ollama_without_the_model_fails_before_benchmarking(self):
        with MockServer(ollama_ok, tags={"models": [{"name": "llama3:8b"}]}) as server:
            result, rows, _ = self.run_script(OLLAMA_SCRIPT, server.url, "-ResourceIntervalMs", "0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("not installed", result.stderr)
        self.assertIsNone(rows)
        self.assertFalse(server.posts())


if __name__ == "__main__":
    unittest.main()
