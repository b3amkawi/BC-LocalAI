"""Tests around the AI-007 failure-injection script that need neither Ollama nor a GPU.

The injection itself needs a real Ollama and is run by hand (docs/ai/ai-failure-test-results.md).
These tests keep the script safe and the evidence honest: the script parses, only ever kills the
process it started, refuses non-loopback addresses, and the committed evidence covers what the
Issue requires with every check passing and every Expected revision recorded.
"""
import json
import os
import re
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "test-ollama-failures.ps1"
EVIDENCE = ROOT / "docs" / "ai" / "ollama-failures-evidence.json"
INITIAL = ROOT / "docs" / "ai" / "ollama-failures-evidence-initial.json"
POLICY = ROOT / "docs" / "ai" / "ai-failure-policy.md"
PWSH = shutil.which("pwsh")
TEXT = SCRIPT.read_text(encoding="utf-8")


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class TestScriptParses(unittest.TestCase):
    def test_script_has_valid_syntax(self):
        cmd = "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:PARSE_TARGET,[ref]$null,[ref]$e); if($e.Count){$e|%{$_.Message}; exit 1}"
        out = subprocess.run([PWSH, "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=60, env={**os.environ, "PARSE_TARGET": str(SCRIPT)})
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)

    def test_help_documents_the_safety_rules(self):
        self.assertIn("DEDICATED Ollama instance", TEXT)
        self.assertIn("never stopped or", TEXT)


class TestScriptSafety(unittest.TestCase):
    def test_only_the_instance_it_started_is_killed(self):
        kills = re.findall(r"taskkill[^\n]*", TEXT)
        self.assertEqual(len(kills), 1)
        self.assertIn("$script:server.Id", kills[0])

    def test_runner_kill_is_limited_to_the_ollama_install_folder(self):
        self.assertIn("-like '*\\Ollama\\*'", TEXT)

    def test_dedicated_port_is_not_the_default_port(self):
        self.assertIn("[int]$Port = 11436", TEXT)

    def test_all_urls_are_checked_as_loopback(self):
        self.assertIn("Assert-LoopbackUrl $base", TEXT)
        self.assertIn("Assert-LoopbackUrl $MainUrl", TEXT)

    def test_memory_exhaustion_is_not_injected(self):
        self.assertIn("memory exhaustion", TEXT)
        self.assertNotRegex(TEXT, r"num_ctx\s*=\s*\d{5,}")

    def test_server_is_always_stopped_in_finally(self):
        self.assertRegex(TEXT, r"finally \{\s*Stop-Instance\s*\}")

    def test_no_credentials_in_the_script(self):
        self.assertNotRegex(TEXT, r"(?i)(password|secret|api[_-]?key)\s*=")


@unittest.skipUnless(EVIDENCE.exists(), "evidence is created by running the script")
class TestEvidence(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.final = json.loads(EVIDENCE.read_text(encoding="utf-8"))

    def test_covers_at_least_five_types_three_times_each(self):
        self.assertGreaterEqual(len(self.final["types"]), 5)
        for t in self.final["types"]:
            self.assertGreaterEqual(len(t["repetitions"]), 3, t["id"])

    def test_ten_types_six_times_each(self):
        self.assertEqual(len(self.final["types"]), 10)
        for t in self.final["types"]:
            self.assertEqual(len(t["repetitions"]), 6, t["id"])

    def test_every_check_of_every_repetition_passed(self):
        self.assertTrue(self.final["all_passed"])
        for t in self.final["types"]:
            for r in t["repetitions"]:
                self.assertTrue(r["passed"], (t["id"], r["repetition"]))
                self.assertTrue(r["checks"])

    def test_expected_is_recorded_for_every_type(self):
        for t in self.final["types"]:
            self.assertTrue(t["expected"], t["id"])

    def test_recovery_times_are_recorded_where_ollama_has_to_recover(self):
        by_id = {t["id"]: t for t in self.final["types"]}
        for tid in ("F01", "F02", "F03"):
            self.assertIsNotNone(by_id[tid]["recovery_summary"], tid)
            self.assertGreaterEqual(by_id[tid]["recovery_summary"]["n"], 3)

    def test_no_prompt_text_or_secret_in_the_evidence(self):
        blob = EVIDENCE.read_text(encoding="utf-8")
        self.assertNotRegex(blob, r"(?i)api[_-]?key|password")

    def test_main_instance_was_not_the_target(self):
        self.assertNotEqual(self.final["environment"]["dedicated_port"], 11434)

    @unittest.skipUnless(INITIAL.exists(), "initial evidence kept for the record")
    def test_every_changed_expectation_is_recorded(self):
        initial = json.loads(INITIAL.read_text(encoding="utf-8"))
        failed = {t["id"] for t in initial["types"] if not t["passed"]}
        recorded = " ".join(r["id"] for r in self.final["expected_revisions"])
        for tid in failed:
            self.assertIn(tid, recorded, f"{tid} failed first but its Expected change is not recorded")
        for r in self.final["expected_revisions"]:
            self.assertTrue(r["reason"].strip())


@unittest.skipUnless(POLICY.exists(), "policy document")
class TestPolicyDocument(unittest.TestCase):
    TEXT = POLICY.read_text(encoding="utf-8") if POLICY.exists() else ""

    def test_every_error_code_has_a_retry_rule(self):
        for code in ("AI_UNAVAILABLE", "AI_TIMEOUT", "AI_PROVIDER_ERROR", "AI_INVALID_RESPONSE", "AI_RESPONSE_TOO_LARGE"):
            self.assertIn(code, self.TEXT)

    def test_no_silent_model_change_or_external_fallback(self):
        self.assertIn("ห้ามมี fallback", self.TEXT)
        self.assertIn("ต้องได้รับอนุมัติ", self.TEXT)

    def test_queue_policy_has_both_status_codes(self):
        self.assertIn("429", self.TEXT)
        self.assertIn("503", self.TEXT)

    def test_user_messages_have_no_internal_details(self):
        messages = self.TEXT.split("ข้อความที่แสดงผู้ใช้ (D4)", 1)[1].split("\n## ", 1)[0]
        self.assertIn("AI_UNAVAILABLE", messages)
        table = chr(10).join(line for line in messages.splitlines() if line.startswith("|"))  # the messages themselves
        self.assertNotRegex(table, r"127\.0\.0\.1|11434|localhost|Exception|stack|wsarecv")


if __name__ == "__main__":
    unittest.main()
