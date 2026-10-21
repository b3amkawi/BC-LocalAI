"""Tests for the CI gate scripts in scripts/ci (DEVOPS-001).

The gates are only worth having if they fail when they should, so most tests feed them
bad input and expect a non-zero result. Strings that look like secrets are built at run
time so the repository itself never contains them.
"""
import contextlib
import http.server
import io
import json
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

def quiet(fn, *args):
    """Call a gate's main() without letting its report mix into the unittest output."""
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return fn(*args)


ROOT = Path(__file__).resolve().parents[1]
CI = ROOT / "scripts" / "ci"
sys.path.insert(0, str(CI))

import check_doc_links  # noqa: E402
import check_test_counts  # noqa: E402
import scan_secrets  # noqa: E402

PWSH = shutil.which("pwsh")

BASELINE = {"allowed_skipped": 0, "min_tests": {"dotnet": 10, "node": 5, "python": 20}}
DOTNET_OK = "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 3 s - X.dll (net10.0)\n"
NODE_OK = "ℹ tests 6\nℹ suites 0\nℹ pass 6\nℹ fail 0\nℹ cancelled 0\nℹ skipped 0\nℹ todo 0\n"
PY_OK = "Ran 25 tests in 1.2s\n\nOK\n"


def fake_key_header() -> str:
    return "-----BEGIN " + "RSA PRIVATE KEY-----"


def fake_aws_id() -> str:
    return "AKIA" + "ABCDEFGHIJKLMNOP"


def fake_github_token() -> str:
    return "ghp_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8"


class TestCountGate(unittest.TestCase):
    def run_gate(self, dotnet=DOTNET_OK, node=NODE_OK, python=PY_OK, baseline=BASELINE):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / "b.json").write_text(json.dumps(baseline), encoding="utf-8")
            (tmp / "d.log").write_text(dotnet, encoding="utf-8")
            (tmp / "n.log").write_text(node, encoding="utf-8")
            (tmp / "p.log").write_text(python, encoding="utf-8")
            return quiet(check_test_counts.main, [
                "--baseline", str(tmp / "b.json"), "--dotnet", str(tmp / "d.log"),
                "--node", str(tmp / "n.log"), "--python", str(tmp / "p.log"),
            ])

    def test_all_good_passes(self):
        self.assertEqual(self.run_gate(), 0)

    def test_dotnet_failure_fails(self):
        bad = "Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12, Duration: 3 s - X.dll\n"
        self.assertEqual(self.run_gate(dotnet=bad), 1)

    def test_dotnet_count_drop_fails(self):
        low = "Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 3 s - X.dll\n"
        self.assertEqual(self.run_gate(dotnet=low), 1)

    def test_dotnet_skipped_fails(self):
        skipped = "Passed!  - Failed:     0, Passed:    10, Skipped:     2, Total:    12, Duration: 3 s - X.dll\n"
        self.assertEqual(self.run_gate(dotnet=skipped), 1)

    def test_node_failure_fails(self):
        self.assertEqual(self.run_gate(node=NODE_OK.replace("fail 0", "fail 1")), 1)

    def test_node_count_drop_fails(self):
        self.assertEqual(self.run_gate(node=NODE_OK.replace("tests 6", "tests 4")), 1)

    def test_python_skipped_fails(self):
        # The case that hid 68 skipped tests when pwsh was missing.
        self.assertEqual(self.run_gate(python="Ran 25 tests in 1.2s\n\nOK (skipped=20)\n"), 1)

    def test_python_failure_fails(self):
        self.assertEqual(self.run_gate(python="Ran 25 tests in 1.2s\n\nFAILED (failures=1)\n"), 1)

    def test_python_errors_count_as_failures(self):
        self.assertEqual(self.run_gate(python="Ran 25 tests in 1.2s\n\nFAILED (errors=2)\n"), 1)

    def test_python_count_drop_fails(self):
        self.assertEqual(self.run_gate(python="Ran 19 tests in 1.2s\n\nOK\n"), 1)

    def test_unparseable_log_is_an_error_not_a_pass(self):
        self.assertEqual(self.run_gate(dotnet="build failed\n"), 2)

    def test_allowed_skipped_can_be_raised_in_the_baseline(self):
        baseline = {**BASELINE, "allowed_skipped": 5}
        self.assertEqual(self.run_gate(python="Ran 25 tests in 1.2s\n\nOK (skipped=3)\n", baseline=baseline), 0)

    def test_python_total_parsing(self):
        self.assertEqual(check_test_counts.parse_python("Ran 1 test in 0.1s\n\nOK\n")["total"], 1)

    def test_multiple_dotnet_projects_are_summed(self):
        text = DOTNET_OK + DOTNET_OK
        self.assertEqual(check_test_counts.parse_dotnet(text)["total"], 24)


class TestNamedSkips(unittest.TestCase):
    BASE = {"allowed_skipped": 0, "allowed_skips": {"python": ["test_codex_only"]},
            "min_tests": {"dotnet": 10, "node": 5, "python": 20}}
    LOG = ("test_codex_only (m.C.test_codex_only) ... skipped 'Codex bundled PowerShell is not installed'\n"
           "test_other (m.C.test_other) ... ok\n\nRan 25 tests in 1.2s\n\nOK (skipped=1)\n")

    def gate(self, python_log, baseline=None):
        return TestCountGate().run_gate(python=python_log, baseline=baseline or self.BASE)

    def test_the_named_skip_is_allowed(self):
        self.assertEqual(self.gate(self.LOG), 0)

    def test_a_different_skipped_test_fails(self):
        log = self.LOG.replace("test_codex_only (m.C.test_codex_only)", "test_new_one (m.C.test_new_one)")
        self.assertEqual(self.gate(log), 1)

    def test_an_extra_skipped_test_fails_even_with_the_allowed_one(self):
        log = self.LOG.replace("Ran 25", "test_x (m.C.test_x) ... skipped 'no reason'\nRan 25").replace("skipped=1", "skipped=2")
        self.assertEqual(self.gate(log), 1)

    def test_without_the_allowance_the_same_log_fails(self):
        self.assertEqual(self.gate(self.LOG, baseline=BASELINE), 1)

    def test_the_skipped_names_are_parsed(self):
        self.assertEqual(check_test_counts.parse_python(self.LOG)["skipped_tests"], ["test_codex_only"])


class TestSecretScan(unittest.TestCase):
    def scan(self, files: dict) -> int:
        with tempfile.TemporaryDirectory() as tmp:
            for name, content in files.items():
                p = Path(tmp) / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text(content, encoding="utf-8")
            return quiet(scan_secrets.main, ["--root", tmp])

    def test_clean_repo_passes(self):
        self.assertEqual(self.scan({"a.md": "# hello\nnothing here\n"}), 0)

    def test_private_key_block_detected(self):
        self.assertEqual(self.scan({"k.txt": fake_key_header() + "\nMIIB\n"}), 1)

    def test_aws_key_id_detected(self):
        self.assertEqual(self.scan({"c.txt": "id: " + fake_aws_id() + "\n"}), 1)

    def test_github_token_detected(self):
        self.assertEqual(self.scan({"c.txt": "t=" + fake_github_token() + "\n"}), 1)

    def test_generic_password_assignment_detected(self):
        self.assertEqual(self.scan({"app.json": '{"password": "Zk8w3Qm91LpT"}\n'}), 1)

    def test_connection_string_password_detected(self):
        self.assertEqual(self.scan({"c.cfg": "Server=db1;Database=x;Uid=u;" + "Pw" + "d=Hunter22;\n"}), 1)

    def test_allow_marker_skips_the_line(self):
        self.assertEqual(self.scan({"t.txt": 'password = "Zk8w3Qm91LpT"  # ci-allow-secret\n'}), 0)

    def test_placeholder_values_are_not_secrets(self):
        self.assertEqual(self.scan({"d.md": 'password = "your-password-here"\napi_key = "<api-key>"\n'}), 0)

    def test_short_values_are_ignored(self):
        self.assertEqual(self.scan({"d.txt": 'token = "abc"\n'}), 0)

    def test_test_directories_skip_only_the_generic_rule(self):
        self.assertEqual(self.scan({"tests/t.py": 'password = "Zk8w3Qm91LpT"\n'}), 0)
        self.assertEqual(self.scan({"tests/t.py": fake_key_header() + "\n"}), 1)

    def test_binary_files_are_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "x.bin").write_bytes(b"\0\0\0" + fake_key_header().encode())
            self.assertEqual(quiet(scan_secrets.main, ["--root", tmp]), 0)

    def test_output_never_contains_the_secret_value(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "c.txt").write_text('password = "Zk8w3Qm91LpT"\n', encoding="utf-8")
            out = subprocess.run([sys.executable, str(CI / "scan_secrets.py"), "--root", tmp], capture_output=True, text=True)
        self.assertEqual(out.returncode, 1)
        self.assertNotIn("Zk8w3Qm91LpT", out.stdout + out.stderr)


class TestDocLinks(unittest.TestCase):
    def check(self, files: dict) -> int:
        with tempfile.TemporaryDirectory() as tmp:
            for name, content in files.items():
                p = Path(tmp) / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text(content, encoding="utf-8")
            return quiet(check_doc_links.main, ["--root", tmp])

    def test_valid_relative_link_passes(self):
        self.assertEqual(self.check({"a.md": "[b](docs/b.md)", "docs/b.md": "x"}), 0)

    def test_missing_target_fails(self):
        self.assertEqual(self.check({"a.md": "[b](docs/missing.md)"}), 1)

    def test_link_relative_to_the_file_not_the_root(self):
        self.assertEqual(self.check({"docs/a.md": "[b](b.md)", "docs/b.md": "x"}), 0)

    def test_anchor_part_is_ignored_but_file_is_checked(self):
        self.assertEqual(self.check({"a.md": "[b](b.md#section)", "b.md": "x"}), 0)
        self.assertEqual(self.check({"a.md": "[b](nope.md#section)"}), 1)

    def test_external_links_are_not_fetched(self):
        self.assertEqual(self.check({"a.md": "[x](https://example.invalid/none) [m](mailto:a@b.c)"}), 0)

    def test_links_in_code_blocks_are_ignored(self):
        text = "```\n[x](missing.md)\n```\n`[y](missing2.md)`\n"
        self.assertEqual(self.check({"a.md": text}), 0)

    def test_image_links_are_checked(self):
        self.assertEqual(self.check({"a.md": "![i](img/none.png)"}), 1)

    def test_percent_encoded_paths_resolve(self):
        self.assertEqual(self.check({"a.md": "[b](my%20file.md)", "my file.md": "x"}), 0)

    def test_pure_anchor_link_passes(self):
        self.assertEqual(self.check({"a.md": "[top](#top)"}), 0)


@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class TestLiveEvidenceScript(unittest.TestCase):
    SCRIPT = CI / "new-live-evidence.ps1"

    @classmethod
    def setUpClass(cls):
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                body = {"/api/version": {"version": "0.0.0-test"},
                        "/api/tags": {"models": [{"name": "qwen3:4b", "digest": "abcdef1234567890"}]}}.get(self.path)
                self.send_response(200 if body else 404)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(body or {}).encode())

            def log_message(self, *a):
                pass

        cls.server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        try:
            cls.server.shutdown()
        finally:
            cls.server.server_close()
            cls.thread.join(timeout=5)

    def run_script(self, *args):
        return subprocess.run([PWSH, "-NoProfile", "-File", str(self.SCRIPT), *args], capture_output=True, text=True, timeout=60)

    def test_block_contains_digest_version_and_hash(self):
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
            f.write("hello")
            name = f.name
        out = self.run_script("-Files", name, "-OllamaUrl", f"http://127.0.0.1:{self.port}")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("Ollama: 0.0.0-test", out.stdout)
        self.assertIn("digest abcdef123456", out.stdout)
        self.assertIn("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", out.stdout)

    def test_missing_file_fails(self):
        out = self.run_script("-Files", "does-not-exist.xyz", "-OllamaUrl", f"http://127.0.0.1:{self.port}")
        self.assertNotEqual(out.returncode, 0)

    def test_non_loopback_ollama_is_refused(self):
        out = self.run_script("-Files", __file__, "-OllamaUrl", "http://192.0.2.10:11434")
        self.assertNotEqual(out.returncode, 0)
        self.assertIn("non-loopback", out.stderr)


class TestLiveEvidenceCleanup(unittest.TestCase):
    def test_server_cleanup_closes_the_listening_socket(self):
        # A separate fixture class cannot interfere with the script tests' shared server.
        fixture = type("IsolatedLiveEvidenceServer", (TestLiveEvidenceScript,), {})
        fixture.setUpClass()
        try:
            listener = fixture.server.socket
            self.assertGreaterEqual(listener.fileno(), 0)
        finally:
            fixture.tearDownClass()
        self.assertEqual(listener.fileno(), -1)
        self.assertFalse(fixture.thread.is_alive())


class TestWorkflowFile(unittest.TestCase):
    TEXT = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")

    def test_runs_on_pull_requests_and_main(self):
        self.assertIn("pull_request:", self.TEXT)
        self.assertIn("branches: [main]", self.TEXT)

    def test_actions_are_pinned_to_full_commit_shas(self):
        import re
        for line in self.TEXT.splitlines():
            if "uses:" in line:
                self.assertRegex(line, r"@[0-9a-f]{40}\b", line)

    def test_no_secrets_or_write_permissions(self):
        self.assertNotRegex(self.TEXT, r"\$\{\{\s*secrets\.")
        self.assertIn("contents: read", self.TEXT)

    def test_trial_branch_pushes_do_not_cancel_each_other(self):
        self.assertIn("startsWith(github.ref, 'refs/heads/ci-trial/') && github.sha", self.TEXT)

    def test_does_not_require_ollama(self):
        self.assertNotIn("ollama serve", self.TEXT.lower())
        self.assertNotIn("11434", self.TEXT)


if __name__ == "__main__":
    unittest.main()
