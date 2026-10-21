"""AI-010 offline regressions. No HTTP, GPU, DB, actual accounts or documents."""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import retrieval_eval as evaluation

GEN_SPEC = importlib.util.spec_from_file_location("retrieval_fixtures", ROOT / "tests/retrieval/build_fixtures.py")
fixtures = importlib.util.module_from_spec(GEN_SPEC)
GEN_SPEC.loader.exec_module(fixtures)
CLI = ROOT / "scripts/test-retrieval.py"


class FixtureSetup:
    def setUp(self):
        self.corpus = evaluation.load_json(ROOT / "tests/retrieval/documents.json")
        self.dataset = evaluation.load_json(ROOT / "tests/retrieval/questions.json")


class FixtureTests(FixtureSetup, unittest.TestCase):
    def test_distribution_and_sources_are_validated(self):
        result = evaluation.validate_dataset(self.dataset, self.corpus)
        self.assertEqual(result["cases"], 60)
        self.assertEqual(result["languages"], {"th": 30, "en": 30})
        self.assertEqual(set(result["categories"].values()), {15})
        self.assertEqual(len(self.corpus["documents"]), 78)

    def test_fixture_rebuild_is_reproducible_and_attacks_are_not_results(self):
        for filename, expected in fixtures.build().items():
            self.assertEqual(evaluation.load_json(ROOT / "tests/retrieval" / filename), expected)
        attacks = fixtures.build()["injection-cases.json"]
        self.assertEqual(len(attacks["cases"]), 10)
        self.assertTrue(all(case["live_tested"] is False for case in attacks["cases"]))
        self.assertEqual(len({case["payload"] for case in attacks["cases"]}), 10)

    def test_multi_answers_are_independently_calculated_from_documents(self):
        import re
        lookup = {doc["id"]: doc for doc in self.corpus["documents"]}
        for case in self.dataset["cases"]:
            if case["category"] != "multi":
                continue
            with self.subTest(case=case["id"]):
                # Read the recorded values from source text, not expected metadata.
                values = [int(re.search(r"(?:มีค่า |records )(\d+)", lookup[s]["text"])[1])
                          for s in case["expected"]["sources"]]
                wanted = sum(values)
                actual = int(re.match(r"\d+", case["expected"]["accepted_answers"][0])[0])
                self.assertEqual(actual, wanted)

    def test_conflict_documents_really_disagree(self):
        import re
        lookup = {doc["id"]: doc for doc in self.corpus["documents"]}
        for case in self.dataset["cases"]:
            if case["category"] != "conflict":
                continue
            with self.subTest(case=case["id"]):
                values = [re.search(r"(?:ประกาศ | is )(\d+)", lookup[s]["text"])[1]
                          for s in case["expected"]["sources"]]
                self.assertNotEqual(*values)

    def test_dataset_rejects_duplicate_ids_unknown_sources_and_denied_sources(self):
        for mutate in (
            lambda d: d["cases"].__setitem__(1, deepcopy(d["cases"][0])),
            lambda d: d["cases"][0]["expected"].__setitem__("sources", ["SYN-Z99"]),
            lambda d: d["cases"][0]["expected"].__setitem__("sources", ["SYN-R03"]),
            lambda d: d["cases"][0]["expected"].__setitem__("status", "not_found"),
        ):
            with self.subTest(mutation=mutate):
                changed = deepcopy(self.dataset)
                mutate(changed)
                with self.assertRaises(evaluation.ValidationError):
                    evaluation.validate_dataset(changed, self.corpus)

    def test_corpus_requires_explicit_nonempty_known_acl(self):
        for invalid in (None, [], ["unknown"], ["reader", "reader"], [{}], [["reader"]]):
            with self.subTest(acl=invalid):
                corpus = deepcopy(self.corpus)
                corpus["documents"][0]["allowed_roles"] = invalid
                with self.assertRaises(evaluation.ValidationError):
                    evaluation.validate_corpus(corpus)

    def test_corpus_refuses_non_synthetic_and_oversized_data(self):
        for mutate in (
            lambda c: c.__setitem__("synthetic", False),
            lambda c: c["documents"][0].__setitem__("text", "x" * (evaluation.MAX_TEXT_BYTES + 1)),
            lambda c: c["documents"][0].__setitem__("text", "\ud800"),
        ):
            with self.subTest(mutation=mutate):
                corpus = deepcopy(self.corpus)
                mutate(corpus)
                with self.assertRaises(evaluation.ValidationError):
                    evaluation.validate_corpus(corpus)

    def test_wrong_json_types_fail_as_validation_errors(self):
        for field in ("category", "language", "role"):
            for value in ([], {}, True, None):
                with self.subTest(field=field, value=value):
                    dataset = deepcopy(self.dataset)
                    dataset["cases"][0][field] = value
                    with self.assertRaises(evaluation.ValidationError):
                        evaluation.validate_dataset(dataset, self.corpus)

    def test_grading_does_not_modify_the_frozen_ground_truth(self):
        original = evaluation.fingerprint(self.dataset)
        case = self.dataset["cases"][0]
        evaluation.grade_answer(case, {"answer": "wrong", "status": "answered", "sources": []}, [])
        self.assertEqual(evaluation.fingerprint(self.dataset), original)


class RetrievalTests(FixtureSetup, unittest.TestCase):
    def test_acl_is_applied_before_ranking_for_three_fixture_roles(self):
        expected = {"reader": {"SYN-R01"}, "engineer": {"SYN-R01", "SYN-R02"},
                    "admin": {"SYN-R01", "SYN-R02", "SYN-R03"}}
        for role, wanted in expected.items():
            with self.subTest(role=role):
                result = evaluation.retrieve("SYN-ACL-BOUNDARY", self.corpus, role)
                self.assertEqual({d["id"] for d in result["documents"]}, wanted)

    def test_denied_document_cannot_change_ranking_or_payload(self):
        first = evaluation.retrieve("SYN-ACL-BOUNDARY", self.corpus, "reader")
        corpus = deepcopy(self.corpus)
        corpus["documents"][-1]["text"] += " SYN-ACL-BOUNDARY" * 200
        second = evaluation.retrieve("SYN-ACL-BOUNDARY", corpus, "reader")
        self.assertEqual(first, second)
        self.assertNotIn("ADMIN_SAMPLE_43", json.dumps(second))

    def test_explicit_sources_are_retrieved_for_all_reference_cases(self):
        for case in self.dataset["cases"]:
            with self.subTest(case=case["id"]):
                result = evaluation.prepare_case(case, self.corpus)
                presented = {doc["id"] for doc in result["selection"]["documents"]}
                self.assertTrue(set(case["expected"]["sources"]).issubset(presented))
                self.assertFalse(result["selection"]["token_policy_verified"])
                self.assertNotIn("accepted_answers", result["request_proposal"]["prompt"])

    def test_byte_budget_counts_utf8_and_json_escaping_without_cutting_text(self):
        case = self.dataset["cases"][0]
        full = evaluation.retrieve(case["question"], self.corpus, case["role"], top_k=1)
        exact = full["context_bytes"]
        selected = evaluation.retrieve(case["question"], self.corpus, case["role"], top_k=1, max_context_bytes=exact)
        self.assertEqual(full, selected)
        small = evaluation.retrieve(case["question"], self.corpus, case["role"], max_context_bytes=2)
        self.assertEqual(small["documents"], [])
        self.assertTrue(small["budget_omitted_ids"])
        self.assertEqual(small["context_bytes"], 2)
        self.assertEqual(len(evaluation.encoded_documents(full["documents"])), exact)

    def test_unknown_role_and_invalid_limits_fail_closed(self):
        for kwargs in ({"role": "unknown"}, {"role": "reader", "max_context_bytes": 0},
                       {"role": "reader", "top_k": True}, {"role": "reader", "top_k": 21}):
            with self.subTest(kwargs=kwargs), self.assertRaises(evaluation.ValidationError):
                evaluation.retrieve("SYN-S01", self.corpus, **kwargs)

    def test_injection_payload_is_serialized_as_document_data_only(self):
        corpus = deepcopy(self.corpus)
        attack = fixtures.build()["injection-cases.json"]["cases"][2]["payload"]
        corpus["documents"][0]["text"] += "\n" + attack
        result = evaluation.prepare_case(self.dataset["cases"][0], corpus)
        proposal = result["request_proposal"]
        self.assertEqual(proposal["system"], evaluation.SYSTEM)
        document = next(d for d in json.loads(proposal["prompt"])["documents"] if d["id"] == "SYN-S01")
        self.assertIn(attack, document["text"])
        # This proves transport separation only, NOT model injection resistance.


class GraderTests(unittest.TestCase):
    def setUp(self):
        self.case = {"expected": {"status": "answered", "accepted_answers": ["Tokyo"], "sources": ["SYN-S01"]}}

    def result(self, answer="Tokyo", sources=None, status="answered"):
        return {"status": status, "answer": answer, "sources": ["SYN-S01"] if sources is None else sources}

    def test_whole_answer_accepts_only_format_variation(self):
        self.assertEqual(evaluation.grade_answer(self.case, self.result(" TOKYO. "), ["SYN-S01"]), [])

    def test_expected_word_inside_wrong_or_ambiguous_answer_is_rejected(self):
        for wrong in ("It is false that Tokyo is the capital of Japan; Paris is.",
                      "Tokyo is not the capital; Paris is.", "Tokyo or Paris", "Tokyo? No."):
            with self.subTest(answer=wrong):
                self.assertIn("answer_not_accepted", evaluation.grade_answer(self.case, self.result(wrong), ["SYN-S01"]))

    def test_wrong_missing_extra_duplicate_or_unpresented_citations_fail(self):
        for sources, presented in (([], ["SYN-S01"]), (["SYN-S02"], ["SYN-S02"]),
                                   (["SYN-S01", "SYN-S02"], ["SYN-S01", "SYN-S02"]),
                                   (["SYN-S01", "SYN-S01"], ["SYN-S01"]), (["SYN-S01"], [])):
            with self.subTest(sources=sources, presented=presented):
                self.assertTrue(evaluation.grade_answer(self.case, self.result(sources=sources), presented))

    def test_wrong_status_empty_thinking_extra_keys_or_nested_result_fails(self):
        for result in (self.result(status="conflict"), self.result(""), self.result("<think>Tokyo</think>"),
                       {**self.result(), "thinking": "sample"}, [self.result()], self.result(sources="SYN-S01")):
            with self.subTest(result=result):
                self.assertTrue(evaluation.grade_answer(self.case, result, ["SYN-S01"]))

    def test_abstention_requires_no_citations(self):
        case = {"expected": {"status": "not_found", "accepted_answers": ["No information found"], "sources": []}}
        answer = self.result("No information found", [], "not_found")
        self.assertEqual(evaluation.grade_answer(case, answer, []), [])
        answer["sources"] = ["SYN-S01"]
        self.assertTrue(evaluation.grade_answer(case, answer, ["SYN-S01"]))

    def test_json_duplicate_keys_nonfinite_and_malformed_are_rejected(self):
        for text in ('{"answer":"Tokyo","answer":"Paris"}', '{"n":NaN}', '{"n":Infinity}', '{', '[[['):
            with self.subTest(text=text), self.assertRaises(evaluation.ValidationError):
                evaluation.strict_json(text)

    def test_numbers_and_units_cannot_be_normalized_into_a_different_fact(self):
        case = {"expected": {"status": "answered", "accepted_answers": ["10 hours"], "sources": ["SYN-S01"]}}
        for wrong in ("10 minutes", "100 hours", "1.0 hours", "10 hours but actually 20 hours"):
            with self.subTest(answer=wrong):
                self.assertTrue(evaluation.grade_answer(case, self.result(wrong), ["SYN-S01"]))


class CliTests(unittest.TestCase):
    def run_cli(self, *args):
        return subprocess.run([sys.executable, "-X", "utf8", str(CLI), *map(str, args)],
                              capture_output=True, text=True, encoding="utf-8", cwd=ROOT, timeout=20)

    def test_validate_has_no_model_results_and_exit_zero(self):
        result = self.run_cli("validate")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["scope"], "offline_dataset_validation")

    def test_prepare_preserves_truthful_scope_and_never_sends_ground_truth(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "prepared.json"
            result = self.run_cli("prepare", "--output", output)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = evaluation.load_json(output)
            self.assertFalse(report["live_verified"])
            self.assertFalse(report["issue_complete"])
            self.assertEqual(len(report["prepared_cases"]), 60)
            self.assertNotIn("accepted_answers", json.dumps(report))

    def envelope(self, rows):
        return {"dataset_sha256": evaluation.fingerprint(evaluation.load_json(ROOT / "tests/retrieval/questions.json")),
                "corpus_sha256": evaluation.fingerprint(evaluation.load_json(ROOT / "tests/retrieval/documents.json")),
                "responses": rows}

    def test_missing_responses_cannot_produce_success(self):
        with tempfile.TemporaryDirectory() as directory:
            responses, output = Path(directory) / "responses.json", Path(directory) / "report.json"
            responses.write_text(json.dumps(self.envelope([])), encoding="utf-8")
            result = self.run_cli("grade", "--responses", responses, "--output", output)
            self.assertEqual(result.returncode, 1, result.stderr)
            report = evaluation.load_json(output)
            self.assertEqual(report["failed"], 60)
            self.assertFalse(report["live_verified"])

    def test_duplicate_or_unknown_response_ids_fail_without_overwriting_evidence(self):
        for ids in (("single-01", "single-01"), ("unknown",)):
            with self.subTest(ids=ids), tempfile.TemporaryDirectory() as directory:
                responses, output = Path(directory) / "responses.json", Path(directory) / "report.json"
                responses.write_text(json.dumps(self.envelope([{"id": identifier, "result": {}} for identifier in ids])), encoding="utf-8")
                output.write_text("previous evidence", encoding="utf-8")
                result = self.run_cli("grade", "--responses", responses, "--output", output)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(output.read_text(), "previous evidence")

    def test_wrong_fingerprint_and_overwriting_inputs_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            responses, output = Path(directory) / "responses.json", Path(directory) / "report.json"
            envelope = self.envelope([])
            envelope["dataset_sha256"] = "0" * 64
            responses.write_text(json.dumps(envelope), encoding="utf-8")
            result = self.run_cli("grade", "--responses", responses, "--output", output)
            self.assertEqual(result.returncode, 2)
            result = self.run_cli("prepare", "--output", ROOT / "tests/retrieval/questions.json")
            self.assertEqual(result.returncode, 2)

    def test_error_output_does_not_echo_arbitrary_input(self):
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory) / "invalid.json"
            corpus.write_text('{"synthetic": "DO_NOT_ECHO_SAMPLE"}', encoding="utf-8")
            result = self.run_cli("validate", "--corpus", corpus)
            self.assertEqual(result.returncode, 2)
            self.assertNotIn("DO_NOT_ECHO_SAMPLE", result.stdout + result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_fixture_answers_can_pass_but_report_never_claims_live_verification(self):
        dataset = evaluation.load_json(ROOT / "tests/retrieval/questions.json")
        rows = [{"id": case["id"], "result": {
            "status": case["expected"]["status"], "answer": case["expected"]["accepted_answers"][0],
            "sources": case["expected"]["sources"]}} for case in dataset["cases"]]
        with tempfile.TemporaryDirectory() as directory:
            responses, output = Path(directory) / "fixture.json", Path(directory) / "report.json"
            responses.write_text(json.dumps(self.envelope(rows), ensure_ascii=False), encoding="utf-8")
            result = self.run_cli("grade", "--responses", responses, "--output", output)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = evaluation.load_json(output)
            self.assertEqual(report["passed"], 60)
            self.assertIn("not_live_evidence", report["scope"])
            self.assertFalse(report["live_verified"])
            self.assertFalse(report["issue_complete"])

    def test_one_wrong_response_means_nonzero_exit_even_with_the_right_word(self):
        dataset = evaluation.load_json(ROOT / "tests/retrieval/questions.json")
        rows = [{"id": case["id"], "result": {
            "status": case["expected"]["status"], "answer": case["expected"]["accepted_answers"][0],
            "sources": case["expected"]["sources"]}} for case in dataset["cases"]]
        rows[0]["result"]["answer"] += " is false; the actual value is 999."
        with tempfile.TemporaryDirectory() as directory:
            responses, output = Path(directory) / "fixture.json", Path(directory) / "report.json"
            responses.write_text(json.dumps(self.envelope(rows), ensure_ascii=False), encoding="utf-8")
            result = self.run_cli("grade", "--responses", responses, "--output", output)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(evaluation.load_json(output)["failed"], 1)

    def test_missing_output_parent_has_a_safe_error_and_no_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "not-created" / "report.json"
            result = self.run_cli("prepare", "--output", output)
            self.assertEqual(result.returncode, 2)
            self.assertIn("output_parent_missing", result.stderr)
            self.assertFalse(output.exists())

    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell 7 required for the convenience launcher")
    def test_powershell_launcher_preserves_success_and_failure_exit_codes(self):
        wrapper = ROOT / "scripts/test-retrieval.ps1"
        for arguments, expected in ((["-Command", "validate"], 0), (["-Command", "prepare"], 2)):
            with self.subTest(arguments=arguments):
                result = subprocess.run([shutil.which("pwsh"), "-NoProfile", "-File", str(wrapper), *arguments],
                                        capture_output=True, text=True, encoding="utf-8", cwd=ROOT, timeout=20)
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
