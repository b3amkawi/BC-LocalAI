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
COLD_SCRIPT = ROOT / "scripts" / "benchmark" / "cold-start-benchmark.ps1"
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
        self.loaded_models = []
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
                elif self.path == "/api/ps":
                    self.send_json(200, {"models": outer.loaded_models})
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

    def test_min_samples_raises_rounds_so_every_scenario_reaches_the_target(self):
        with MockServer(backend_ok) as server:
            result, rows, summary = self.run_script(
                BACKEND_SCRIPT, server.url, "-Concurrency", "1,2,5,10", "-Profiles", "short", "-Rounds", "2",
                "-MinSamples", "12", "-WarmupRequests", "0", "-ResourceIntervalMs", "0", timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        by_level = {s["concurrency"]: s for s in summary["scenarios"]}
        self.assertEqual({level: s["rounds"] for level, s in by_level.items()}, {1: 12, 2: 6, 5: 3, 10: 2})
        self.assertEqual({level: s["requests"] for level, s in by_level.items()}, {1: 12, 2: 12, 5: 15, 10: 20})
        self.assertEqual(summary["parameters"]["min_samples_per_scenario"], 12)

    def test_prompt_pool_has_fifteen_unique_prompts_per_profile(self):
        source = (ROOT / "scripts" / "benchmark" / "BenchmarkCommon.psm1").read_text(encoding="utf-8")
        result = subprocess.run(
            [PWSH, "-NoProfile", "-Command",
             f"Import-Module '{ROOT / 'scripts' / 'benchmark' / 'BenchmarkCommon.psm1'}'; Get-BenchmarkPrompts | ConvertTo-Json -Depth 3"],
            capture_output=True, encoding="utf-8", timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr)
        pools = json.loads(result.stdout)
        self.assertEqual(set(pools), {"short", "medium", "long"})
        for name, prompts in pools.items():
            with self.subTest(profile=name):
                self.assertEqual(len(prompts), 15)
                self.assertEqual(len(set(prompts)), 15)
                self.assertGreaterEqual(sum(bool(re.search(r"[฀-๿]", prompt)) for prompt in prompts), 2)
        self.assertEqual(pools["short"][0], "What is the capital of Japan? Answer with one word.")

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
            if "opposite of hot" in prompt:
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
        # The first request of a run is the warm-up/cold request (short-1); measured ones start at short-2.
        self.assertEqual(outcomes["short-5"]["outcome"], "truncated")
        self.assertEqual(outcomes["short-2"]["outcome"], "server_error")
        self.assertEqual(outcomes["short-2"]["error_code"], "OLLAMA_ERROR")
        self.assertEqual(outcomes["short-3"]["outcome"], "client_timeout")
        scenario = summary["scenarios"][0]
        self.assertEqual((scenario["success"], scenario["truncated"], scenario["server_error"], scenario["client_timeout"]),
                         (2, 1, 1, 1))
        self.assertEqual(scenario["timeout_rate"], 0.2)

    def test_backend_run_separates_server_timeout_from_server_error(self):
        def reply(body):
            if "one word" in body["message"]:
                return {"status": 504, "body": {"error": {"code": "AI_TIMEOUT", "message": "AI service timed out"}}}
            if "number only" in body["message"]:
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
                "-WarmupRequests", "0", "-ResourceIntervalMs", "200", "-MinAvailableMemoryMB", "1048576",
                "-MemoryRecoveryWaitSec", "3")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("STOPPED", result.stderr)
        self.assertIn("available RAM", summary["aborted"])
        # It waited for the memory to recover before giving up, and recorded the wait.
        self.assertGreaterEqual(summary["memory_pauses"]["total_seconds"], 3)
        self.assertEqual(summary["memory_pauses"]["count"], 1)
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

    def run_cold_script(self, target, base_url, ollama_url, shim_exit_code=0, repeats=3):
        folder = tempfile.TemporaryDirectory(prefix="cold-")
        self.addCleanup(folder.cleanup)
        shim_dir = Path(folder.name) / "bin"
        shim_dir.mkdir()
        # A fake `ollama stop` so the real Ollama is never touched.
        (shim_dir / "ollama.cmd").write_text(f"@echo off\r\nexit /b {shim_exit_code}\r\n", encoding="ascii", newline="")
        csv_path = Path(folder.name) / "cold.csv"
        env = {**__import__("os").environ, "PATH": f"{shim_dir};{__import__('os').environ['PATH']}"}
        result = subprocess.run(
            [PWSH, "-NoProfile", "-File", str(COLD_SCRIPT), "-Target", target, "-Repeats", str(repeats),
             "-BaseUrl", base_url, "-OllamaUrl", ollama_url, "-UnloadWaitSec", "2", "-OutputCsv", str(csv_path)],
            capture_output=True, encoding="utf-8", errors="replace", timeout=180, env=env)
        rows = list(csv.DictReader(csv_path.open(encoding="utf-8"))) if csv_path.exists() else None
        return result, rows

    def test_cold_start_script_records_one_cold_row_per_repetition(self):
        with MockServer(ollama_ok) as server:
            result, rows = self.run_cold_script("ollama", server.url, server.url)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual([row["repetition"] for row in rows], ["1", "2", "3"])
        self.assertTrue(all(row["phase"] == "cold" and row["outcome"] == "success" for row in rows))
        # Each repetition sends the cold request plus one warm request whose row the script discards.
        self.assertEqual(len(server.posts()), 6)
        self.assertIn("SUMMARY: target=ollama repeats=3 success=3", result.stdout)

    def test_cold_start_script_fails_when_the_model_stays_loaded(self):
        with MockServer(ollama_ok) as server:
            server.loaded_models = [{"name": "qwen3:4b"}]
            result, rows = self.run_cold_script("ollama", server.url, server.url, repeats=1)
        self.assertEqual(result.returncode, 1)
        self.assertIn("still loaded", result.stderr)
        self.assertIsNone(rows)
        self.assertFalse(server.posts())

    def test_ollama_without_the_model_fails_before_benchmarking(self):
        with MockServer(ollama_ok, tags={"models": [{"name": "llama3:8b"}]}) as server:
            result, rows, _ = self.run_script(OLLAMA_SCRIPT, server.url, "-ResourceIntervalMs", "0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("not installed", result.stderr)
        self.assertIsNone(rows)
        self.assertFalse(server.posts())


if __name__ == "__main__":
    unittest.main()
