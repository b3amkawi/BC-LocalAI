"""Exercise AI-003 evaluator failures against a loopback mock Backend."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-chat-e2e.ps1"
PWSH = shutil.which("pwsh")
PER_CATEGORY = 40
TOTAL = PER_CATEGORY * 4


def dataset():
    cases = []
    for category in ("thai", "english", "math", "edge"):
        language = {"thai": "th", "english": "en"}.get(category, "any")
        answer = {"thai": "ตกลง", "math": "42"}.get(category, "ok")
        for number in range(PER_CATEGORY):
            cases.append({
                "id": f"{category}-{number:02}",
                "category": category,
                "language": language,
                "prompt": f"safe prompt {category} {number} 😊",
                "accepted_answers": [answer],
            })
    return {"version": 1, "api": "POST http://localhost:5290/api/chat", "cases": cases}


def valid_response(request):
    category = request["message"].split()[2]
    answer = {"thai": "ตกลง", "math": "42"}.get(category, "ok")
    return {"response": answer, "model": "qwen3:4b"}


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class ChatE2EScriptTests(unittest.TestCase):
    def run_script(self, reply_factory=None, fixture=None, extra_args=None,
                   base_url=None, invalid_output=False, expect_evidence=True, timeout_sec=10):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                content_length = int(self.headers.get("Content-Length", "0"))
                request = json.loads(self.rfile.read(content_length).decode("utf-8"))
                requests.append((self.path, request))
                reply = reply_factory(len(requests), request) if reply_factory else {}
                if "delay" in reply:
                    time.sleep(reply["delay"])
                body = reply.get("body", valid_response(request))
                payload = reply.get("raw", json.dumps(body, ensure_ascii=False)).encode("utf-8")
                self.send_response(reply.get("status", 200))
                self.send_header("Content-Type", "application/json; charset=utf-8")
                if reply.get("redirect"):
                    self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/forbidden")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                try:
                    self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass  # Expected when the client timeout cancels a delayed request.

            def do_GET(self):
                requests.append((self.path, {}))
                payload = b'{"response":"ok","model":"qwen3:4b"}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
            thread.start()
            try:
                with tempfile.TemporaryDirectory(prefix="chat-e2e-") as folder:
                    folder_path = Path(folder)
                    dataset_path = folder_path / "cases.json"
                    evidence_path = folder_path / "evidence.json"
                    if invalid_output:
                        evidence_path = folder_path / "missing-folder" / "evidence.json"
                    dataset_path.write_text(json.dumps(fixture or dataset(), ensure_ascii=False), encoding="utf-8")
                    arguments = [
                        PWSH, "-NoProfile", "-File", str(SCRIPT),
                        "-BaseUrl", base_url or f"http://127.0.0.1:{server.server_port}",
                        "-DatasetPath", str(dataset_path),
                        "-OutputPath", str(evidence_path),
                        "-TimeoutSec", str(timeout_sec),
                    ]
                    arguments.extend(extra_args or [])
                    result = subprocess.run(arguments, capture_output=True, encoding="utf-8",
                                            errors="replace", timeout=90)
                    evidence = None
                    if evidence_path.exists():
                        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
                    elif expect_evidence:
                        self.fail("Evaluator did not create evidence.\n"
                                  f"exit={result.returncode}\nstdout={result.stdout}\nstderr={result.stderr}")
                    return result, evidence, requests
            finally:
                server.shutdown()
                thread.join(timeout=5)

    def test_all_valid_backend_responses_return_zero(self):
        result, evidence, requests = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(evidence["total"], TOTAL)
        self.assertEqual(evidence["passed"], TOTAL)
        self.assertEqual(evidence["failed"], 0)
        self.assertEqual(evidence["warm_sample_count"], TOTAL - 1)
        self.assertTrue(all(path == "/api/chat" for path, _ in requests))
        self.assertTrue(all(set(body) == {"message"} for _, body in requests))
        self.assertTrue(all("😊" in body["message"] for _, body in requests))
        self.assertEqual(evidence["results"][0]["phase"], "cold_candidate")

    def test_invalid_response_matrix_returns_nonzero_and_keeps_raw_evidence(self):
        valid = {"response": "ตกลง", "model": "qwen3:4b"}
        failures = {
            1: {"body": None},
            2: {"body": []},
            3: {"body": [valid]},
            4: {"body": "ตกลง"},
            5: {"body": False},
            6: {"body": {**valid, "response": None}},
            7: {"body": {**valid, "response": ["ตกลง"]}},
            8: {"body": {"model": "qwen3:4b"}},
            9: {"body": {"Response": "ตกลง", "model": "qwen3:4b"}},
            10: {"body": {"response": "ตกลง", "Model": "qwen3:4b"}},
            11: {"body": {**valid, "model": "mock"}},
            12: {"body": {**valid, "model": ["qwen3:4b"]}},
            13: {"body": {**valid, "response": ""}},
            14: {"body": {**valid, "response": "  \n"}},
            15: {"body": {**valid, "response": '{"answer":"ตกลง"}'}},
            16: {"body": {**valid, "response": '```json\n{"answer":"ตกลง"}\n```'}},
            17: {"body": {**valid, "response": '["ตกลง"]'}},
            18: {"body": {**valid, "response": '```json\n["ตกลง"]\n```'}},
            19: {"body": {**valid, "response": "<think>ตกลง</think>"}},
            20: {"body": {**valid, "response": "ตกลง </THINK>"}},
            21: {"body": {**valid, "response": "ตกลง System.InvalidOperationException: failure"}},
            22: {"body": {**valid, "response": "ตกลง stack trace: internal method"}},
            23: {"body": {**valid, "response": "English answer only"}},
            24: {"body": {**valid, "response": "ไม่ทราบ"}},
            25: {"status": 503},
            26: {"raw": "{malformed JSON"},
            27: {"raw": ""},
            PER_CATEGORY + 1: {"body": {**valid, "response": "ตกลง"}},
            PER_CATEGORY * 2 + 1: {"body": {**valid, "response": "43"}},
        }
        result, evidence, requests = self.run_script(
            lambda index, _: failures.get(index, {})
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(len(requests), TOTAL)
        self.assertEqual(evidence["failed"], len(failures))
        for index in failures:
            with self.subTest(case=index):
                entry = evidence["results"][index - 1]
                self.assertFalse(entry["passed"], entry)
                self.assertTrue(entry["errors"], entry)
                self.assertIsInstance(entry["raw_body"], str)
        for index, expected_error in (
            (15, "nested JSON"), (16, "nested JSON"),
            (17, "nested JSON"), (18, "nested JSON"),
            (19, "thinking marker"), (20, "thinking marker"),
            (21, "system details"), (22, "system details"),
            (23, "language"), (24, "accepted_answers"),
            (PER_CATEGORY + 1, "language"),
            (PER_CATEGORY * 2 + 1, "accepted_answers"),
        ):
            with self.subTest(guard=expected_error, case=index):
                self.assertIn(expected_error, " ".join(evidence["results"][index - 1]["errors"]))
        failed_warm = len(failures) - 1
        self.assertEqual(evidence["warm_sample_count"], TOTAL - 1 - failed_warm)

    def test_expected_word_inside_wrong_or_extra_sentence_returns_nonzero(self):
        fixture = dataset()
        fixture["cases"][0]["accepted_answers"] = ["โตเกียว"]
        for offset in range(5):
            fixture["cases"][PER_CATEGORY + offset]["accepted_answers"] = ["Tokyo"]

        def noncanonical_reply(index, request):
            if index == 1:
                return {
                    "body": {
                        "response": "โตเกียวไม่ใช่เมืองหลวงของญี่ปุ่น ปารีสต่างหาก",
                        "model": "qwen3:4b",
                    }
                }
            if index == PER_CATEGORY + 1:
                return {
                    "body": {
                        "response": "Tokyo is not the capital of Japan; Paris is.",
                        "model": "qwen3:4b",
                    }
                }
            if index == PER_CATEGORY + 2:
                return {
                    "body": {
                        "response": "The capital is not Tokyo; it is Paris.",
                        "model": "qwen3:4b",
                    }
                }
            if index == PER_CATEGORY + 3:
                return {
                    "body": {
                        "response": "It is false that Tokyo is the capital of Japan; Paris is.",
                        "model": "qwen3:4b",
                    }
                }
            if index == PER_CATEGORY + 4:
                return {"body": {"response": "Tokyo? No.", "model": "qwen3:4b"}}
            if index == PER_CATEGORY + 5:
                return {"body": {"response": "Tokyo or Paris", "model": "qwen3:4b"}}
            return {}

        result, evidence, requests = self.run_script(
            noncanonical_reply, fixture=fixture
        )

        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(len(requests), TOTAL)
        self.assertEqual(evidence["failed"], 6)
        for result_index in (0, *range(PER_CATEGORY, PER_CATEGORY + 5)):
            with self.subTest(result_index=result_index):
                entry = evidence["results"][result_index]
                self.assertFalse(entry["passed"])
                self.assertIn("not one of accepted_answers", " ".join(entry["errors"]))

    def test_normalized_complete_answers_pass(self):
        fixture = dataset()
        fixture["cases"][PER_CATEGORY]["accepted_answers"] = ["Pacific Ocean"]
        fixture["cases"][PER_CATEGORY + 1]["accepted_answers"] = ["Tokyo"]
        fixture["cases"][PER_CATEGORY * 2]["accepted_answers"] = ["-8", "−8"]
        fixture["cases"][PER_CATEGORY * 2 + 1]["accepted_answers"] = ["-8", "−8"]
        replies = {
            PER_CATEGORY + 1: "  pacific\n ocean. ",
            PER_CATEGORY + 2: "TOKYO!",
            PER_CATEGORY * 2 + 1: "−8",
            PER_CATEGORY * 2 + 2: "-8",
        }

        result, evidence, _ = self.run_script(
            lambda index, _: {"body": {"response": replies[index], "model": "qwen3:4b"}}
            if index in replies else {},
            fixture=fixture,
        )

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(evidence["failed"], 0)

    def test_invalid_datasets_fail_before_any_request(self):
        fixtures = {}
        too_small = dataset()
        too_small["cases"].pop()
        fixtures["category too small"] = too_small
        for label, field, value in (
            ("duplicate ID", "id", "thai-01"),
            ("empty prompt", "prompt", " "),
            ("unknown category", "category", "other"),
            ("unknown language", "language", "xx"),
            ("missing answers", "accepted_answers", None),
            ("empty answers", "accepted_answers", []),
            ("answers not a list", "accepted_answers", "ตกลง"),
            ("blank answer", "accepted_answers", [" "]),
            ("non-string answer", "accepted_answers", [42]),
        ):
            fixture = dataset()
            fixture["cases"][0][field] = value
            fixtures[label] = fixture
        for label, fixture in fixtures.items():
            with self.subTest(label=label):
                result, evidence, requests = self.run_script(fixture=fixture, expect_evidence=False)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertFalse(requests)
                self.assertIsNone(evidence)

    def test_rejects_unsafe_backend_urls_before_any_request(self):
        for base_url in (
            "http://192.168.1.10:5290", "ftp://localhost:5290", "http://localhost:11434",
            "http://user:password@localhost:5290", "http://localhost:5290/other",
            "http://localhost:5290/?query=value", "http://localhost:5290/#fragment",
        ):
            with self.subTest(base_url=base_url):
                result, evidence, requests = self.run_script(base_url=base_url, expect_evidence=False)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertFalse(requests)
                self.assertIsNone(evidence)

    def test_redirect_is_not_followed(self):
        result, evidence, requests = self.run_script(
            lambda index, _: {"status": 307, "redirect": True} if index == 1 else {}
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(evidence["failed"], 1)
        self.assertEqual(evidence["results"][0]["http_status"], 307)
        self.assertEqual(len(requests), TOTAL)
        self.assertTrue(all(path == "/api/chat" for path, _ in requests))

    def test_timeout_fails_one_case_and_continues(self):
        result, evidence, requests = self.run_script(
            lambda index, _: {"delay": 2} if index == 1 else {},
            timeout_sec=1,
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(evidence["failed"], 1)
        self.assertEqual(len(requests), TOTAL)
        self.assertTrue(evidence["results"][0]["errors"])

    def test_invalid_evidence_destination_returns_nonzero(self):
        result, evidence, requests = self.run_script(invalid_output=True, expect_evidence=False)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertFalse(requests)
        self.assertIsNone(evidence)

    def test_cold_start_note_is_recorded_without_warmup_requests(self):
        note = "Model unloaded manually immediately before this mock-only run."
        result, evidence, requests = self.run_script(extra_args=["-ColdStartNote", note])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(evidence["results"][0]["phase"], "cold")
        self.assertEqual(evidence["cold_start_note"], note)
        self.assertEqual(len(requests), TOTAL)


if __name__ == "__main__":
    unittest.main()
