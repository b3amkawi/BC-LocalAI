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
