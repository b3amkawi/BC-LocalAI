"""Tests of the QA-002 catalog, runner and evidence (tests/security). They run without .NET."""
from __future__ import annotations

import importlib.util
import json
import re
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SECURITY = ROOT / "tests" / "security"
CSHARP = ROOT / "BC-LocalAI.Tests" / "Qa002"
EVIDENCE = ROOT / "docs" / "qa" / "qa-002-evidence.json"
DEFECTS = ROOT / "docs" / "qa" / "qa-002-defects.md"


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, SECURITY / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cases_module = load("qa002_cases")
docs_module = load("make_qa002_docs")
runner = load("run_qa002")
CATALOG = json.loads((SECURITY / "qa002-cases.json").read_text(encoding="utf-8"))
IDS = [c["id"] for c in CATALOG["cases"]]
PLAIN = "QA2-A-01-login-ok"  # a case with no documented defect


class CatalogTests(unittest.TestCase):
    def test_json_is_generated_from_the_python_catalog(self):
        self.assertEqual(cases_module.render(), (SECURITY / "qa002-cases.json").read_text(encoding="utf-8"))

    def test_ids_are_unique_and_groups_known(self):
        self.assertEqual(len(IDS), len(set(IDS)))
        for case in CATALOG["cases"]:
            self.assertIn(case["group"], CATALOG["groups"], case["id"])
            self.assertTrue(case["id"].startswith("QA2-"), case["id"])

    def test_every_case_states_its_expected_result_and_basis(self):
        for case in CATALOG["cases"]:
            self.assertGreaterEqual(len(case["expected"]), 3, case["id"])
            self.assertGreater(len(case["basis"]), 1, case["id"])
            if case["status"] is not None:
                self.assertIn(case["status"], range(200, 600), case["id"])

    def test_matrix_document_is_generated_from_the_catalog(self):
        self.assertEqual(docs_module.matrix(), (ROOT / "docs" / "qa" / "qa-002-test-matrix.md").read_text(encoding="utf-8"))

    def test_matrix_covers_every_role_for_the_chat_endpoints(self):
        for endpoint in ("chat", "stream"):
            for role in ("anonymous", "user", "admin", "logged-out", "disabled", "idle-expired", "absolute-expired", "role-changed"):
                self.assertIn(f"QA2-M-{endpoint}-{role}", IDS)
        for key in ("conv-get-other", "conv-patch-other", "conv-delete-other", "conv-msgs-other", "admin-only", "me", "logout", "health"):
            self.assertTrue(any(i.startswith(f"QA2-M-{key}-") for i in IDS), key)

    def test_unauthorized_chat_cases_expect_zero_ollama_calls(self):
        for case in CATALOG["cases"]:
            if case["id"].startswith(("QA2-M-chat-", "QA2-M-stream-")) and case["status"] == 401:
                self.assertEqual(case["ollama"], 0, case["id"])

    def test_csharp_tests_use_only_catalogued_ids(self):
        used = set()
        for path in CSHARP.glob("*.cs"):
            used |= set(re.findall(r"QA2-[A-Z]+-[A-Za-z0-9-]*[A-Za-z0-9]", path.read_text(encoding="utf-8")))
        unknown = sorted(i for i in used if i not in IDS and not any(c.startswith(i + "-") for c in IDS))  # f-string templates are prefixes
        self.assertEqual(unknown, [])

    def test_every_catalogued_case_has_a_test(self):
        text = "\n".join(p.read_text(encoding="utf-8") for p in CSHARP.glob("*.cs"))
        missing = []
        for case_id in IDS:
            if case_id in text:
                continue
            if case_id.startswith("QA2-C-") and any(case_id.startswith(f"QA2-C-{e}-") for e in ("chat", "stream", "conv-create", "conv-patch", "conv-delete", "logout")):
                continue  # built from the endpoint x variant tables of Qa002CsrfTests
            if case_id.startswith("QA2-F-") and any(case_id.startswith(t) for t in re.findall(r"(QA2-F-[A-Za-z0-9-]*-)\{", text)):
                continue  # built from the operation / token-kind lists of Qa002FrontendTests
            if case_id.startswith("QA2-M-"):
                rest = case_id[len("QA2-M-"):]
                for role in ("anonymous", "user", "admin", "logged-out", "disabled", "idle-expired", "absolute-expired", "role-changed"):
                    if rest.endswith("-" + role) and f'"{rest[: -len(role) - 1]}"' in text:
                        break
                else:
                    missing.append(case_id)
                continue  # built from the role list of Qa002MatrixTests
            if case_id.startswith("QA2-MS-") and f'InlineData("{case_id[len("QA2-MS-"):]}")' in text:
                continue  # theory data of Qa002StreamTests
            missing.append(case_id)
        self.assertEqual(missing, [])


class RunnerMergeTests(unittest.TestCase):
    def merge(self, runs):
        with tempfile.TemporaryDirectory() as tmp:
            files = []
            for number, lines in enumerate(runs, 1):
                path = Path(tmp) / f"run-{number}.jsonl"
                path.write_text("\n".join(json.dumps(line) for line in lines), encoding="utf-8")
                files.append(path)
            return runner.merge(files, [])

    @staticmethod
    def line(case_id, verdict, status=200):
        return {"id": case_id, "run": "1", "verdict": verdict, "actualStatus": status, "actualCode": None, "actualOllama": 0, "detail": "", "defect": None}

    def test_a_case_passes_only_when_every_run_passes(self):
        merged = self.merge([[self.line(PLAIN, "PASS")], [self.line(PLAIN, "PASS")]])
        case = merged["cases"][0]
        self.assertEqual((case["verdict"], case["passedRuns"], case["runs"]), ("PASS", 2, 2))

    def test_one_failing_run_fails_the_case(self):
        merged = self.merge([[self.line(PLAIN, "PASS")], [self.line(PLAIN, "FAIL", 500)]])
        self.assertEqual(merged["cases"][0]["verdict"], "FAIL")
        self.assertEqual(merged["summary"]["inconsistentAcrossRuns"], [PLAIN])
        self.assertIn(PLAIN, merged["summary"]["failWithoutDefect"])

    def test_cases_without_evidence_are_reported_not_hidden(self):
        merged = self.merge([[self.line(PLAIN, "PASS")]])
        self.assertEqual(len(merged["summary"]["notExecuted"]), len(IDS) - 1)

    def test_observations_are_not_counted_as_passes(self):
        merged = self.merge([[self.line(PLAIN, "OBSERVED")]])
        self.assertEqual((merged["summary"]["pass"], merged["summary"]["observed"]), (0, 1))


@unittest.skipUnless(EVIDENCE.exists(), "evidence is produced by tests/security/run_qa002.py")
class EvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = EVIDENCE.read_text(encoding="utf-8")
        cls.evidence = json.loads(cls.text)

    def test_every_case_was_executed_at_least_twice(self):
        self.assertGreaterEqual(self.evidence["runs"], 2)
        self.assertEqual(self.evidence["summary"]["notExecuted"], [])
        for case in self.evidence["cases"]:
            self.assertEqual(case["recordedRuns"], self.evidence["runs"], case["id"])

    def test_no_failure_is_left_without_a_defect_record(self):
        self.assertEqual(self.evidence["summary"]["failWithoutDefect"], [])
        defects = DEFECTS.read_text(encoding="utf-8")
        for case in self.evidence["cases"]:
            if case["defect"]:
                self.assertIn(case["defect"], defects, case["id"])

    def test_evidence_holds_no_cookie_token_or_credential(self):
        for pattern in (r"__Host-BC\.[A-Za-z]+=[^;\s\"]{6,}", r"CfDJ8[A-Za-z0-9_-]{10,}", r"AQAAAA[A-Za-z0-9+/=]{20,}", r"(?i)password\"?\s*[:=]\s*\"[^\"]{6,}"):
            self.assertIsNone(re.search(pattern, self.text), pattern)

    def test_summary_matches_the_cases(self):
        summary = self.evidence["summary"]
        cases = self.evidence["cases"]
        self.assertEqual(summary["pass"], sum(c["verdict"] == "PASS" for c in cases))
        self.assertEqual(summary["fail"], sum(c["verdict"] == "FAIL" for c in cases))
        self.assertEqual(summary["catalogCases"], len(IDS))


if __name__ == "__main__":
    unittest.main()
