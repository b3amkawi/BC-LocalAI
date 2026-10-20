"""Check the real AI-003 dataset and independently calculate its math answers."""

import ast
from fractions import Fraction
import json
from pathlib import Path
import re
import unittest


DATASET = Path(__file__).resolve().parents[1] / "docs" / "ai" / "chat-evaluation-cases.json"


def calculate(node):
    if isinstance(node, ast.Expression):
        return calculate(node.body)
    if isinstance(node, ast.Constant) and type(node.value) is int:
        return Fraction(node.value)
    if isinstance(node, ast.BinOp):
        left, right = calculate(node.left), calculate(node.right)
        if isinstance(node.op, ast.Add):
            return left + right
        if isinstance(node.op, ast.Sub):
            return left - right
        if isinstance(node.op, ast.Mult):
            return left * right
        if isinstance(node.op, ast.Div):
            return left / right
    raise ValueError("Unsupported arithmetic expression")


class ChatDatasetTests(unittest.TestCase):
    def test_real_dataset_has_forty_unique_cases_per_category(self):
        cases = json.loads(DATASET.read_text(encoding="utf-8"))["cases"]
        self.assertEqual(len(cases), 160)
        self.assertEqual(len({case["id"] for case in cases}), 160)
        for category in ("thai", "english", "math", "edge"):
            self.assertEqual(sum(case["category"] == category for case in cases), 40)

    def test_every_case_lists_complete_accepted_answers(self):
        cases = json.loads(DATASET.read_text(encoding="utf-8"))["cases"]
        for case in cases:
            with self.subTest(case=case["id"]):
                self.assertNotIn("expected_regex", case)
                answers = case["accepted_answers"]
                self.assertIsInstance(answers, list)
                self.assertTrue(answers)
                self.assertTrue(all(isinstance(a, str) and a.strip() for a in answers))
                self.assertEqual(len(answers), len(set(answers)))

    def test_accepted_answers_follow_the_prompt_instruction(self):
        """Every accepted answer must itself obey the format the prompt asks for."""
        cases = json.loads(DATASET.read_text(encoding="utf-8"))["cases"]
        for case in cases:
            prompt = case["prompt"]
            for answer in case["accepted_answers"]:
                with self.subTest(case=case["id"], answer=answer):
                    unit = re.search(r"Include the words? (.+?)\. Reply with only the number", prompt)
                    if unit:
                        number = r"(?:\d+|[a-z]+(?:[- ][a-z]+)?)"
                        self.assertRegex(answer, rf"^{number} {re.escape(unit[1])}$")
                    if "ordinal word" in prompt:
                        self.assertNotRegex(answer, r"\d")
                    if "one English word" in prompt:
                        self.assertRegex(answer, r"^[A-Za-z]+$")
                    if "ตัวเลขอารบิกตามด้วยหน่วย" in prompt:
                        thai_unit = re.search(r"กี่(\S+)\s+ตอบ", prompt)[1]
                        self.assertRegex(answer, rf"^\d+ ?{re.escape(thai_unit)}$")
                    if "ตอบเฉพาะตัวเลขอารบิกเท่านั้น" in prompt or "Answer with the number only." in prompt:
                        self.assertRegex(answer, r"^[-−]?\d+$")
                    if case["language"] == "th":
                        self.assertNotRegex(answer, r"[A-Za-z]")
                    if case["language"] == "en":
                        self.assertRegex(answer, r"^[A-Za-z0-9 '\-]+$")

    def test_math_expected_answers_match_independent_arithmetic(self):
        cases = json.loads(DATASET.read_text(encoding="utf-8"))["cases"]
        for case in (case for case in cases if case["category"] == "math"):
            with self.subTest(case=case["id"]):
                match = re.match(r"^(?:What is )?([0-9()\s+×÷*/-]+)(?:\?| เท่ากับ)", case["prompt"])
                self.assertIsNotNone(match)
                expression = match[1].replace("×", "*").replace("÷", "/").strip()
                result = calculate(ast.parse(expression, mode="eval"))
                self.assertEqual(result.denominator, 1)
                accepted = {answer.replace("−", "-") for answer in case["accepted_answers"]}
                self.assertEqual(accepted, {str(result.numerator)})


if __name__ == "__main__":
    unittest.main()
