"""Tests for scripts/test-ollama-context.py (AI-008). No Ollama needed: pure functions and a fake loopback server."""
import importlib.util
import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-ollama-context.py"
spec = importlib.util.spec_from_file_location("ollama_context", SCRIPT)
ctx = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ctx)


class PromptTests(unittest.TestCase):
    def test_codes_are_planted_at_start_middle_and_end_before_the_question(self):
        prompt = ctx.make_prompt("english", 40, ["111111", "222222", "333333"])
        a, b, c = (prompt.index(code) for code in ("111111", "222222", "333333"))
        self.assertLess(a, b)
        self.assertLess(b, c)
        self.assertLess(a, len(prompt) * 0.2)
        self.assertGreater(c, len(prompt) * 0.8)
        self.assertTrue(prompt.endswith(ctx.QUESTION["english"]))

    def test_recalled_reports_each_code(self):
        self.assertEqual([True, False, True], ctx.recalled("a 123456 and 789012", ["123456", "000000", "789012"]))

    def test_single_prompt_matches_the_be007_template(self):
        prompt = ctx.single_prompt([("q1", "a1")], "now")
        self.assertEqual(
            ctx.HEADER + "\n<conversation>\nUser: q1\nAssistant: a1\n</conversation>\nCurrent question:\nnow", prompt)


class TruncationRuleTests(unittest.TestCase):
    def test_flags_the_cut_size_when_the_text_is_far_longer(self):
        self.assertTrue(ctx.truncation_rule(2050, 20000))      # 20000 bytes > 3,000 tokens at the most generous ratio
        self.assertTrue(ctx.truncation_rule(2045, 20000))

    def test_does_not_flag_a_prompt_that_is_really_that_short(self):
        self.assertFalse(ctx.truncation_rule(2050, 9000))      # 9000 bytes can legitimately be 2,050 tokens
        self.assertFalse(ctx.truncation_rule(1500, 20000))
        self.assertFalse(ctx.truncation_rule(3900, 20000))

    def test_summary_counts_detection_and_false_alarms(self):
        def row(truncated, rule, first=True):
            return {"language": "english", "num_predict": 256, "expected_tokens": 5000 if truncated else 1000, "truncated": truncated,
                    "rule_says_truncated": rule, "recalled_first": first, "recalled_middle": first, "recalled_last": True}
        summary = ctx.summarise_limits([row(True, True, False), row(True, False, False), row(False, False), row(False, True)])
        self.assertEqual(2, summary["rule"]["truncated_runs"])
        self.assertEqual(1, summary["rule"]["detected"])
        self.assertEqual(1, summary["rule"]["false_alarms_among_untruncated"])
        self.assertEqual(0.5, summary["rule"]["detection_rate"])
        self.assertEqual(0, summary["recall_first_when_cut"])
        self.assertEqual(1, summary["recall_first_when_read_in_full"])


class FakeOllama(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{"version":"0.0-test"}')

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        tokens = len(body.get("prompt", "").split())
        reply = {"response": json.dumps({"answer": "ok"}), "prompt_eval_count": min(tokens, 2050) + 40, "done_reason": "stop",
                 "prompt_eval_duration": 1_000_000_000}
        self.send_response(200)
        self.end_headers()
        self.wfile.write(json.dumps(reply).encode())


class ScriptTests(unittest.TestCase):
    def test_refuses_a_non_loopback_address(self):
        import subprocess
        import sys
        result = subprocess.run([sys.executable, str(SCRIPT), "limits", "--base-url", "http://example.com:11434", "--output", "x.json"],
                                capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("loopback", result.stderr)

    def test_tokens_per_unit_uses_the_difference_of_two_sizes(self):
        server = HTTPServer(("127.0.0.1", 0), FakeOllama)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            base = f"http://127.0.0.1:{server.server_address[1]}"
            per_unit = ctx.tokens_per_unit(base, "english")
            self.assertGreater(per_unit, 0)
        finally:
            server.shutdown()


if __name__ == "__main__":
    unittest.main()
