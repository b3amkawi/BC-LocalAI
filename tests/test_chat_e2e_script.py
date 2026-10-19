"""Regression tests for the AI-003 Backend-only evaluation script."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-chat-e2e.ps1"
PWSH = shutil.which("pwsh")


def dataset():
    cases = []
    for category in ("thai", "english", "math", "edge"):
        for number in range(5):
            cases.append({
                "id": f"{category}-{number}",
                "category": category,
                "language": "any",
                "prompt": f"safe prompt {category} {number}",
                "expected_regex": "^ok$",
            })
    return {"version": 1, "cases": cases}


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class ChatE2EScriptTests(unittest.TestCase):
    def run_script(self, response_factory):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                content_length = int(self.headers["Content-Length"])
                body = json.loads(self.rfile.read(content_length).decode("utf-8"))
                requests.append((self.path, body))
                payload = json.dumps(response_factory(len(requests)), ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                with tempfile.TemporaryDirectory(prefix="chat-e2e-") as folder:
                    folder_path = Path(folder)
                    dataset_path = folder_path / "cases.json"
                    evidence_path = folder_path / "evidence.json"
                    dataset_path.write_text(json.dumps(dataset()), encoding="utf-8")
                    result = subprocess.run(
                        [
                            PWSH, "-NoProfile", "-File", str(SCRIPT),
                            "-BaseUrl", f"http://127.0.0.1:{server.server_port}",
                            "-DatasetPath", str(dataset_path),
                            "-OutputPath", str(evidence_path),
                            "-TimeoutSec", "10",
                        ],
                        capture_output=True,
                        encoding="utf-8",
                        errors="replace",
                        timeout=30,
                    )
                    if not evidence_path.exists():
                        self.fail(
                            "Evaluator did not create evidence.\n"
                            f"exit={result.returncode}\nstdout={result.stdout}\nstderr={result.stderr}"
                        )
                    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
                    return result, evidence, requests
            finally:
                server.shutdown()
                thread.join(timeout=5)

    def test_all_valid_backend_responses_return_zero(self):
        result, evidence, requests = self.run_script(
            lambda _: {"response": "ok", "model": "qwen3:4b"}
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(evidence["total"], 20)
        self.assertEqual(evidence["passed"], 20)
        self.assertEqual(evidence["failed"], 0)
        self.assertTrue(all(path == "/api/chat" for path, _ in requests))
        self.assertTrue(all(set(body) == {"message"} for _, body in requests))

    def test_one_invalid_model_returns_nonzero(self):
        result, evidence, requests = self.run_script(
            lambda index: {"response": "ok", "model": "mock" if index == 1 else "qwen3:4b"}
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(evidence["passed"], 19)
        self.assertEqual(evidence["failed"], 1)
        self.assertIn("expected qwen3:4b", evidence["results"][0]["errors"][0])
        self.assertEqual(len(requests), 20)


if __name__ == "__main__":
    unittest.main()
