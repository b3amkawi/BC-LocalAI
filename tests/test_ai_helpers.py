"""Validate the local chat helper and Windows launcher without loading a model."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


ROOT = Path(__file__).resolve().parents[1]
CHAT = ROOT / "scripts" / "chat-ollama.ps1"
LAUNCHER = ROOT / "scripts" / "open-chat.cmd"
PWSH = shutil.which("pwsh")
VALID = {
    "model": "qwen3:4b", "done": True, "done_reason": "stop",
    "response": json.dumps({"answer": "helper-answer-sentinel"}),
}


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class ChatHelperTests(unittest.TestCase):
    def run_chat(self, reply=None, raw=None, redirect=False):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                requests.append((self.path, json.loads(body.decode("utf-8"))))
                if redirect and self.path == "/api/generate":
                    self.send_response(307)
                    self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/redirect-target")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                data = (raw if raw is not None else json.dumps(reply or VALID)).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
            thread.start()
            try:
                outcome = subprocess.run(
                    [PWSH, "-NoProfile", "-File", str(CHAT), "-Port", str(server.server_port)],
                    input="hello\n/bye\n", capture_output=True, encoding="utf-8",
                    errors="replace", timeout=15,
                )
                return outcome, requests
            finally:
                server.shutdown()
                thread.join(timeout=5)

    def test_valid_final_answer_is_printed(self):
        outcome, requests = self.run_chat()
        self.assertEqual(outcome.returncode, 0, outcome.stdout + outcome.stderr)
        self.assertIn("AI > helper-answer-sentinel", outcome.stdout)
        self.assertEqual(len(requests), 1)
        self.assertEqual(requests[0][0], "/api/generate")
        self.assertEqual(requests[0][1]["prompt"], "hello")
        self.assertEqual(requests[0][1]["model"], "qwen3:4b")
        self.assertFalse(requests[0][1]["stream"])
        self.assertFalse(requests[0][1]["think"])

    def test_invalid_contract_never_prints_answer(self):
        cases = {
            "wrong model": {**VALID, "model": "other"},
            "array model": {**VALID, "model": ["qwen3:4b"]},
            "string done": {**VALID, "done": "true"},
            "incomplete": {**VALID, "done": False},
            "truncated": {**VALID, "done_reason": "length"},
            "array reason": {**VALID, "done_reason": ["stop"]},
            "extra field": {**VALID, "response": '{"answer":"helper-answer-sentinel","extra":1}'},
            "array answer object": {**VALID, "response": '[{"answer":"helper-answer-sentinel"}]'},
            "duplicate answer": {**VALID, "response": '{"answer":"helper-answer-sentinel","answer":"helper-answer-sentinel"}'},
            "wrong answer case": {**VALID, "response": '{"ANSWER":"helper-answer-sentinel"}'},
            "thinking": {**VALID, "response": '{"answer":"<think>helper-answer-sentinel</think>"}'},
            "uppercase thinking": {**VALID, "response": '{"answer":"helper-answer-sentinel</THINK>"}'},
            "blank answer": {**VALID, "response": '{"answer":" "}'},
            "malformed answer": {**VALID, "response": 'helper-answer-sentinel'},
        }
        for label, reply in cases.items():
            with self.subTest(case=label):
                outcome, requests = self.run_chat(reply=reply)
                self.assertEqual(outcome.returncode, 0, outcome.stdout + outcome.stderr)
                self.assertEqual(len(requests), 1)
                self.assertNotIn("AI >", outcome.stdout)
                self.assertNotIn("helper-answer-sentinel", outcome.stdout)
        raw = json.dumps(VALID)[:-1] + ',"model":"qwen3:4b"}'
        outcome, _ = self.run_chat(raw=raw)
        self.assertNotIn("AI >", outcome.stdout)
        self.assertNotIn("helper-answer-sentinel", outcome.stdout)

    def test_redirect_is_not_followed(self):
        outcome, requests = self.run_chat(redirect=True)
        self.assertNotIn("AI >", outcome.stdout)
        self.assertEqual([path for path, _ in requests], ["/api/generate"])


@unittest.skipUnless(os.name == "nt" and PWSH, "Windows and PowerShell 7 are required")
class ChatLauncherTests(unittest.TestCase):
    def run_launcher(self, child_exit, bundled=False):
        with tempfile.TemporaryDirectory(prefix="ai-launcher-") as folder:
            folder_path = Path(folder)
            launcher = folder_path / "open-chat.cmd"
            shutil.copyfile(LAUNCHER, launcher)
            (folder_path / "chat-ollama.ps1").write_text(f"exit {child_exit}\n", encoding="utf-8")
            environment = os.environ.copy()
            system32 = str(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32")
            environment["PATH"] = system32 if bundled else str(Path(PWSH).parent) + os.pathsep + system32
            return subprocess.run(
                [str(Path(system32) / "cmd.exe"), "/d", "/c", str(launcher)],
                env=environment, capture_output=True, encoding="utf-8", errors="replace", timeout=15,
            )

    def test_path_runtime_preserves_child_exit_code(self):
        for expected in (0, 7):
            with self.subTest(exit_code=expected):
                outcome = self.run_launcher(expected)
                self.assertEqual(outcome.returncode, expected, outcome.stdout + outcome.stderr)

    def test_bundled_runtime_preserves_child_exit_code(self):
        bundled = Path(os.environ.get("USERPROFILE", "")) / ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe"
        if not bundled.exists():
            self.skipTest("Codex bundled PowerShell is not installed")
        for expected in (0, 7):
            with self.subTest(exit_code=expected):
                outcome = self.run_launcher(expected, bundled=True)
                self.assertEqual(outcome.returncode, expected, outcome.stdout + outcome.stderr)


if __name__ == "__main__":
    unittest.main()
