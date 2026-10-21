"""Tests for the AI-006 scripts: paired Direct/Backend benchmark orchestration, the summarizer,
and the random-arrival / cancellation benchmark. No Ollama or GPU is needed: the summarizer is
fed synthetic results and the PowerShell scripts run against a small fake Backend."""
import csv
import http.server
import importlib.util
import json
import os
import shutil
import socketserver
import subprocess
import tempfile
import threading
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BENCH = ROOT / "scripts" / "benchmark"
PWSH = shutil.which("pwsh")

spec = importlib.util.spec_from_file_location("summarize_paired", BENCH / "summarize-paired.py")
sp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sp)

HEADER = ["run_id", "target", "phase", "profile", "concurrency", "round", "request_index", "prompt_id", "start_utc", "end_utc", "start_offset_ms",
          "elapsed_ms", "http_status", "outcome", "error_code", "response_chars", "done_reason", "prompt_eval_count", "eval_count", "load_ms",
          "prompt_eval_ms", "eval_ms", "total_ms", "tokens_per_sec", "queue_wait_ms"]


def write_run(directory, slices, overhead_ms=0.0, noise=0.0, outcome_by_prompt=None):
    """Synthetic post-BE-003 results: `slices` paired slices of 20 requests, Backend = Direct + overhead."""
    directory = Path(directory)
    (directory / "slices").mkdir(parents=True, exist_ok=True)
    rows = []
    for k in range(1, slices + 1):
        for target in ("ollama", "backend"):
            run_id = f"run-{k}-{target}"
            base = 1000 + (k % 3) * 5
            for i in range(20):
                latency = base + i + (overhead_ms + noise * (1 if k % 2 else -1) if target == "backend" else 0)
                prompt_id = f"long-{3 if i == 0 else 4}"
                outcome = (outcome_by_prompt or {}).get(prompt_id, "success")
                rows.append({"run_id": run_id, "target": target, "phase": "measured", "profile": "long", "concurrency": 1, "round": i + 1, "request_index": 0,
                             "prompt_id": prompt_id, "elapsed_ms": latency, "outcome": outcome, "error_code": "" if outcome == "success" else "AI_RESPONSE_TOO_LARGE"})
            summary = {"run_id": run_id, "scenarios": [{"target": target, "profile": "long", "concurrency": 1, "wall_seconds": 20.0, "success": 20, "requests": 20,
                                                        "gpu_percent_avg": 80.0, "gpu_percent_max": 95.0, "vram_used_mb_max": 3200.0, "ram_available_mb_min": 1500.0,
                                                        "cpu_percent_avg": 20.0, "cpu_percent_max": 60.0, "ollama_working_set_mb_max": 1200.0}]}
            (directory / "slices" / f"long-c1-{k:03d}-{target}.json").write_text(json.dumps(summary), encoding="utf-8")
    with open(directory / "benchmark-results.csv", "w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=HEADER)
        w.writeheader()
        for r in rows:
            w.writerow({h: r.get(h, "") for h in HEADER})
    return rows


def write_baseline(directory):
    directory = Path(directory)
    rows = []
    for target in ("ollama", "backend"):
        for i in range(10):
            rows.append({"run_id": "base", "target": target, "phase": "measured", "profile": "long", "concurrency": 1, "round": i, "request_index": 0,
                         "prompt_id": "long-3" if i < 3 else "long-4", "elapsed_ms": 900 + i, "outcome": "truncated" if i < 3 else "success", "error_code": ""})
    with open(directory / "base.csv", "w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=HEADER)
        w.writeheader()
        for r in rows:
            w.writerow({h: r.get(h, "") for h in HEADER})
    for target in ("ollama", "backend"):
        (directory / f"base-{target}.json").write_text(json.dumps({"scenarios": [{"target": target, "profile": "long", "concurrency": 1, "throughput_rps": 0.29}]}), encoding="utf-8")
    return directory / "base.csv", [directory / "base-ollama.json", directory / "base-backend.json"]


class TestSummarizer(unittest.TestCase):
    def run_summary(self, **kw):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        post = tmp / "post"
        write_run(post, **kw)
        base_csv, base_json = write_baseline(tmp)
        out = tmp / "summary.json"
        sp.main(["--post", str(post), "--baseline-csv", str(base_csv), "--baseline-summaries", *map(str, base_json), "--output", str(out)])
        return json.loads(out.read_text(encoding="utf-8")), out.with_suffix(".md").read_text(encoding="utf-8")

    def test_nearest_rank_matches_the_powershell_rule(self):
        self.assertEqual(sp.nearest_rank([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 5)
        self.assertEqual(sp.nearest_rank([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10)
        self.assertEqual(sp.nearest_rank([7], 99), 7)
        self.assertIsNone(sp.nearest_rank([], 50))

    def test_throughput_is_successes_over_wall_time(self):
        data, _ = self.run_summary(slices=4)
        post = next(s for s in data["scenarios"] if s["target"] == "ollama")["post"]
        self.assertAlmostEqual(post["throughput_rps"], 1.0)  # 4 slices x 20 successes / 80 s

    def test_baseline_is_carried_for_comparison(self):
        data, md = self.run_summary(slices=4)
        base = next(s for s in data["scenarios"] if s["target"] == "ollama")["baseline"]
        self.assertEqual(base["throughput_rps"], 0.29)
        self.assertEqual(base["outcomes"].get("truncated"), 3)
        self.assertIn("ก่อน", md)

    def test_a_constant_overhead_is_separable_from_zero(self):
        data, _ = self.run_summary(slices=6, overhead_ms=80.0)
        o = data["overhead"][0]
        self.assertTrue(o["separable_from_zero"])
        self.assertAlmostEqual(o["mean_diff_ms"], 80.0, places=0)

    def test_noise_without_overhead_is_not_called_an_overhead(self):
        data, _ = self.run_summary(slices=6, overhead_ms=0.0, noise=100.0)
        self.assertFalse(data["overhead"][0]["separable_from_zero"])

    def test_resource_peaks_come_from_the_slice_files(self):
        data, _ = self.run_summary(slices=2)
        post = next(s for s in data["scenarios"] if s["target"] == "backend")["post"]
        self.assertEqual(post["vram_used_mb_max"], 3200.0)
        self.assertEqual(post["ram_available_mb_min"], 1500.0)

    def test_thai_long_prompts_are_reported_before_and_after(self):
        data, md = self.run_summary(slices=2, outcome_by_prompt={"long-3": "success"})
        row = next(t for t in data["thai_long"] if t["target"] == "ollama" and t["prompt_id"] == "long-3")
        self.assertEqual(row["baseline"], {"truncated": 3})
        self.assertEqual(row["post"], {"success": 2})
        self.assertIn("long-3", md)

    def test_t_critical_values_are_conservative_for_small_samples(self):
        self.assertGreater(sp.t_crit(2), sp.t_crit(10))
        self.assertEqual(sp.t_crit(100), 1.96)


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class TestPowerShellScripts(unittest.TestCase):
    def parse_errors(self, script):
        cmd = "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:PARSE_TARGET,[ref]$null,[ref]$e); if($e.Count){$e|%{$_.Message}; exit 1}"
        return subprocess.run([PWSH, "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=60, env={**os.environ, "PARSE_TARGET": str(script)})

    def test_scripts_parse(self):
        for name in ("run-paired-benchmark.ps1", "arrival-and-cancel-benchmark.ps1"):
            out = self.parse_errors(BENCH / name)
            self.assertEqual(out.returncode, 0, name + out.stdout + out.stderr)

    def test_orchestrator_alternates_resumes_and_is_loopback_only(self):
        text = (BENCH / "run-paired-benchmark.ps1").read_text(encoding="utf-8")
        self.assertIn("$k % 2 -eq 1", text)
        self.assertIn("already complete", text)
        self.assertIn("-AppendCsv", text)
        self.assertIn("Assert-LoopbackBaseUrl $OllamaUrl", text)
        self.assertNotIn("$env:OLLAMA", text)  # never sets Ollama settings itself

    def test_non_loopback_target_is_refused(self):
        out = subprocess.run([PWSH, "-NoProfile", "-File", str(BENCH / "run-paired-benchmark.ps1"), "-OutputDir", tempfile.mkdtemp(), "-OllamaUrl", "http://192.0.2.5:11434"],
                             capture_output=True, text=True, timeout=60)
        self.assertNotEqual(out.returncode, 0)

    def test_the_backend_url_cannot_be_the_ollama_port(self):
        out = subprocess.run([PWSH, "-NoProfile", "-File", str(BENCH / "arrival-and-cancel-benchmark.ps1"), "-Mode", "Arrival", "-Target", "backend",
                              "-BaseUrl", "http://127.0.0.1:11434", "-OutputJson", str(Path(tempfile.mkdtemp()) / "o.json")], capture_output=True, text=True, timeout=60)
        self.assertNotEqual(out.returncode, 0)


class FakeBackend(http.server.BaseHTTPRequestHandler):
    lock = threading.Lock()
    service_s = 0.15

    def log_message(self, *args):
        pass

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        with FakeBackend.lock:  # one at a time, like Ollama
            time.sleep(FakeBackend.service_s)
            body = json.dumps({"response": "ok", "model": "qwen3:4b"}).encode()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except OSError:
                pass


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class TestArrivalAndCancelAgainstFakeBackend(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
            daemon_threads = True
            allow_reuse_address = True

            def handle_error(self, request, client_address):
                pass

        cls.server = Server(("127.0.0.1", 0), FakeBackend)
        cls.url = f"http://127.0.0.1:{cls.server.server_address[1]}"
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def run_script(self, *args):
        out_file = Path(tempfile.mkdtemp()) / "out.json"
        res = subprocess.run([PWSH, "-NoProfile", "-File", str(BENCH / "arrival-and-cancel-benchmark.ps1"), "-Target", "backend", "-BaseUrl", self.url,
                              "-Profile", "short", "-OutputJson", str(out_file), *args], capture_output=True, text=True, timeout=180)
        self.assertEqual(res.returncode, 0, res.stdout + res.stderr)
        return json.loads(out_file.read_text(encoding="utf-8"))

    def test_arrival_mode_reports_latency_and_counts(self):
        data = self.run_script("-Mode", "Arrival", "-Rate", "4", "-DurationSec", "8")
        self.assertEqual(data["mode"], "Arrival")
        self.assertGreater(data["arrived"], 5)
        failed = sum(int(x.split("=")[1]) for x in data["other_failures"])
        self.assertEqual(data["success"] + data["unfinished"] + failed, data["arrived"])
        self.assertIsNotNone(data["latency_p50_ms"])
        self.assertLess(data["latency_p50_ms"], 2000)

    def test_arrival_is_repeatable_with_the_same_seed(self):
        a = self.run_script("-Mode", "Arrival", "-Rate", "3", "-DurationSec", "6", "-Seed", "7")
        b = self.run_script("-Mode", "Arrival", "-Rate", "3", "-DurationSec", "6", "-Seed", "7")
        self.assertEqual(a["arrived"], b["arrived"])

    def test_cancel_mode_compares_control_and_cancel(self):
        data = self.run_script("-Mode", "Cancel", "-Burst", "5", "-CancelCount", "2", "-CancelAfterMs", "300", "-Repeats", "2")
        self.assertEqual(len(data["runs"]), 4)
        self.assertEqual({r["kind"] for r in data["runs"]}, {"control", "cancel"})
        for r in data["runs"]:
            self.assertEqual(r["kept_requests"], 3)
            self.assertEqual(r["kept_success"], 3)
        self.assertIn("control_median_ms", data)
        self.assertIn("cancel_median_ms", data)


if __name__ == "__main__":
    unittest.main()
