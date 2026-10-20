"""HTTP contract tests for the smoke script; no Ollama/model download required.

Run from the repository root: python -m unittest discover -s tests -v
Requires Python 3 and PowerShell 7 (pwsh) on PATH, for development tests only.
"""

import json
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-ollama.ps1"
PWSH = shutil.which("pwsh")
VALID = {
    "model": "qwen3:4b", "done": True, "done_reason": "stop",
    "response": json.dumps({"answer": "สวัสดีครับ!"}, ensure_ascii=False),
}


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class SmokeScriptTests(unittest.TestCase):
    def invoke(self, port, *args):
        return subprocess.run(
            [PWSH, "-NoProfile", "-File", str(SCRIPT), "-Port", str(port), *args],
            capture_output=True, encoding="utf-8", errors="replace", timeout=20,
        )

    def run_endpoint(self, result=None, version=None, status=200, delay=0,
                     invalid_path=False, timeout=5, prior_evidence=None,
                     raw_result=None, redirect_stage=None):
        requests = []
        result = VALID if result is None else result
        version = {"version": "contract-test"} if version is None else version

        class Handler(BaseHTTPRequestHandler):
            def send_json(self, value, code=200):
                data = json.dumps(value, ensure_ascii=False).encode("utf-8")
                self.send_response(code)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass  # Expected when testing a client timeout.

            def do_GET(self):
                if self.path == "/redirect-target":
                    requests.append((self.path, {}))
                if redirect_stage == "version" and self.path == "/api/version":
                    self.send_redirect()
                    return
                self.send_json(version)

            def send_redirect(self):
                self.send_response(307)
                self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/redirect-target")
                self.send_header("Content-Length", "0")
                self.end_headers()

            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                requests.append((self.path, json.loads(body.decode("utf-8"))))
                if redirect_stage == "generate" and self.path == "/api/generate":
                    self.send_redirect()
                    return
                time.sleep(delay)
                if raw_result is None:
                    self.send_json(result, status)
                else:
                    data = raw_result.encode("utf-8")
                    self.send_response(status)
                    self.send_header("Content-Type", "application/json; charset=utf-8")
                    self.send_header("Content-Length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)

            def log_message(self, *args):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                with tempfile.TemporaryDirectory(prefix="ollama-contract-") as folder:
                    path = Path(folder) / "evidence.json"
                    if invalid_path:
                        path = Path(folder) / "missing-directory" / "evidence.json"
                    if prior_evidence is not None:
                        path.write_text(json.dumps(prior_evidence), encoding="utf-8")
                    outcome = self.invoke(server.server_port, "-TimeoutSec", str(timeout),
                                          "-OutputPath", str(path))
                    evidence = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
                    self.assertEqual(list(Path(folder).glob(".ollama-*.tmp")), [])
                    return outcome, evidence, requests
            finally:
                server.shutdown()
                thread.join(timeout=5)

    def assert_failure(self, outcome, evidence, stage):
        self.assertEqual(outcome.returncode, 1, outcome.stdout + outcome.stderr)
        self.assertNotIn("PASS:", outcome.stdout)
        self.assertIn(stage, outcome.stderr)
        self.assertIsNone(evidence, "Failed runs must not produce success evidence")

    def test_success_preserves_utf8_prompt_and_evidence(self):
        outcome, evidence, requests = self.run_endpoint()
        self.assertEqual(outcome.returncode, 0, outcome.stderr)
        self.assertIn("สวัสดีครับ!", outcome.stdout)
        self.assertEqual(requests[0][0], "/api/generate")
        self.assertEqual(requests[0][1]["prompt"], "สวัสดี")
        self.assertFalse(requests[0][1]["stream"])
        self.assertFalse(requests[0][1]["think"])
        self.assertEqual(requests[0][1]["model"], "qwen3:4b")
        self.assertEqual(requests[0][1]["options"], {"temperature": 0, "num_predict": 256})
        self.assertEqual(
            requests[0][1]["format"],
            {
                "type": "object",
                "properties": {"answer": {"type": "string"}},
                "required": ["answer"],
                "additionalProperties": False,
            },
        )
        self.assertEqual(evidence["http_status"], 200)
        self.assertEqual(evidence["result"], VALID)
        self.assertEqual(evidence["ollama_version"], "contract-test")

    def test_rejects_truncated_or_invalid_responses(self):
        cases = {
            "truncated": {**VALID, "done_reason": "length"},
            "not_done": {**VALID, "done": False},
            "string_boolean": {**VALID, "done": "true"},
            "wrong_model": {**VALID, "model": "qwen3:8b"},
            "array_model": {**VALID, "model": ["qwen3:4b"]},
            "array_reason": {**VALID, "done_reason": ["stop"]},
            "invalid_json": {**VALID, "response": "not JSON"},
            "root_array": {**VALID, "response": '[{"answer":"ok"}]'},
            "extra_field": {**VALID, "response": '{"answer":"ok","extra":1}'},
            "wrong_key_case": {**VALID, "response": '{"ANSWER":"ok"}'},
            "empty_answer": {**VALID, "response": '{"answer":" "}'},
            "missing_answer": {**VALID, "response": '{}'},
            "thinking_marker": {**VALID, "response": '{"answer":"<think>trace</think>ok"}'},
            "duplicate_answer": {**VALID, "response": '{"answer":"one","answer":"two"}'},
        }
        for name, result in cases.items():
            with self.subTest(name=name):
                outcome, evidence, _ = self.run_endpoint(result=result)
                self.assert_failure(outcome, evidence, "validating a response")

    def test_rejects_duplicate_outer_contract_fields(self):
        for name, value in (("model", "qwen3:4b"), ("done", True),
                            ("done_reason", "stop"), ("response", VALID["response"])):
            with self.subTest(field=name):
                raw = json.dumps(VALID, ensure_ascii=False)
                raw = raw[:-1] + "," + json.dumps(name) + ":" + json.dumps(value) + "}"
                outcome, evidence, _ = self.run_endpoint(raw_result=raw)
                self.assert_failure(outcome, evidence, "validating a response")

    def test_redirects_are_not_followed(self):
        for stage in ("version", "generate"):
            with self.subTest(stage=stage):
                outcome, evidence, requests = self.run_endpoint(redirect_stage=stage)
                expected_stage = "checking Ollama availability" if stage == "version" else "generating a response"
                self.assert_failure(outcome, evidence, expected_stage)
                self.assertFalse(any(path == "/redirect-target" for path, _ in requests))

    def test_invalid_version_is_not_treated_as_ollama(self):
        outcome, evidence, requests = self.run_endpoint(version={"unrelated": "service"})
        self.assert_failure(outcome, evidence, "checking Ollama availability")
        self.assertEqual(requests, [])

    def test_success_replaces_previous_evidence(self):
        outcome, evidence, _ = self.run_endpoint(prior_evidence={"previous": True})
        self.assertEqual(outcome.returncode, 0, outcome.stderr)
        self.assertEqual(evidence["result"], VALID)
        self.assertNotIn("previous", evidence)

    def test_failed_generation_preserves_previous_evidence(self):
        previous = {"previous": True}
        outcome, evidence, _ = self.run_endpoint(
            result={**VALID, "done_reason": "length"}, prior_evidence=previous,
        )
        self.assertEqual(outcome.returncode, 1, outcome.stdout + outcome.stderr)
        self.assertEqual(evidence, previous)

    def test_model_not_found_has_download_hint(self):
        outcome, evidence, _ = self.run_endpoint(status=404, result={"error": "model not found"})
        self.assert_failure(outcome, evidence, "generating a response")
        self.assertIn("ollama pull qwen3:4b", outcome.stderr)

    def test_request_timeout_fails(self):
        outcome, evidence, _ = self.run_endpoint(delay=3, timeout=1)
        self.assert_failure(outcome, evidence, "generating a response")

    def test_unwritable_evidence_path_has_file_hint(self):
        outcome, evidence, _ = self.run_endpoint(invalid_path=True)
        self.assert_failure(outcome, evidence, "saving evidence")
        self.assertIn("-OutputPath", outcome.stderr)

    def test_unavailable_endpoint_fails(self):
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
            # Bound but not listening: no service is stopped to test refusal.
            outcome = self.invoke(port, "-TimeoutSec", "1")
        self.assert_failure(outcome, None, "checking Ollama availability")


if __name__ == "__main__":
    unittest.main()
