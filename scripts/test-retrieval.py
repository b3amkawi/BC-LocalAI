#!/usr/bin/env python3
"""AI-010 offline validate/prepare/grade commands. No networking or GPU use."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys
import tempfile

from retrieval_eval import (ValidationError, fingerprint, grade_answer, load_json,
                            prepare_case, validate_dataset)

ROOT = Path(__file__).resolve().parents[1]


def write_json(path, value):
    destination = Path(path)
    if not destination.parent.is_dir():
        raise ValidationError("output_parent_missing")
    # Keep a previous artifact intact if serialization/writing fails.
    content = json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    name = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=destination.parent,
                                         prefix=".retrieval-", suffix=".tmp", delete=False) as stream:
            name = stream.name
            stream.write(content)
        os.replace(name, destination)
    finally:
        if name and os.path.exists(name):
            os.unlink(name)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate", "prepare", "grade"))
    parser.add_argument("--corpus", type=Path, default=ROOT / "tests/retrieval/documents.json")
    parser.add_argument("--cases", type=Path, default=ROOT / "tests/retrieval/questions.json")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--responses", type=Path)
    parser.add_argument("--max-context-bytes", type=int, default=8192)
    parser.add_argument("--top-k", type=int, default=4)
    args = parser.parse_args(argv)
    try:
        corpus, dataset = load_json(args.corpus), load_json(args.cases)
        summary = validate_dataset(dataset, corpus)
        if args.command == "validate":
            print(json.dumps({"scope": "offline_dataset_validation", **summary}, ensure_ascii=True))
            return 0
        if args.output is None:
            raise ValidationError("output_required")
        inputs = {args.corpus.resolve(), args.cases.resolve()}
        if args.responses:
            inputs.add(args.responses.resolve())
        if args.output.resolve() in inputs:
            raise ValidationError("output_would_overwrite_input")
        prepared = [prepare_case(case, corpus, max_context_bytes=args.max_context_bytes, top_k=args.top_k)
                    for case in dataset["cases"]]
        report = {
            "schema_version": 1, "created_utc": datetime.now(timezone.utc).isoformat(),
            "scope": "offline_preparation", "live_verified": False, "issue_complete": False,
            "dataset_sha256": fingerprint(dataset), "corpus_sha256": fingerprint(corpus),
            "context_policy": "provisional_byte_budget_AI008_pending",
        }
        if args.command == "prepare":
            report["prepared_cases"] = prepared
        else:
            if not args.responses:
                raise ValidationError("responses_required")
            envelope = load_json(args.responses)
            if not isinstance(envelope, dict) or set(envelope) != {"dataset_sha256", "corpus_sha256", "responses"}:
                raise ValidationError("invalid_response_envelope")
            if any(envelope[key] != report[key] for key in ("dataset_sha256", "corpus_sha256")):
                raise ValidationError("response_fixture_fingerprint_mismatch")
            rows = envelope["responses"]
            if not isinstance(rows, list):
                raise ValidationError("response_rows_required")
            indexed = {}
            known = {case["id"] for case in dataset["cases"]}
            for row in rows:
                if not isinstance(row, dict) or set(row) != {"id", "result"} or not isinstance(row["id"], str):
                    raise ValidationError("invalid_response_row")
                if row["id"] in indexed or row["id"] not in known:
                    raise ValidationError("duplicate_or_unknown_response_id")
                indexed[row["id"]] = row["result"]
            results = []
            for case, item in zip(dataset["cases"], prepared):
                errors = ["response_missing"] if case["id"] not in indexed else grade_answer(
                    case, indexed[case["id"]], [d["id"] for d in item["selection"]["documents"]])
                results.append({"id": case["id"], "category": case["category"],
                                "language": case["language"], "passed": not errors, "errors": errors})
            report["scope"] = "offline_imported_response_grading_not_live_evidence"
            report["results"] = results
            report["passed"] = sum(row["passed"] for row in results)
            report["failed"] = len(results) - report["passed"]
        write_json(args.output, report)
        print(json.dumps({key: report[key] for key in ("scope", "live_verified", "issue_complete", "passed", "failed")
                          if key in report}))
        return 1 if report.get("failed", 0) else 0
    except (ValidationError, OSError, UnicodeError) as error:
        # Do not echo input text, file paths or credentials from arbitrary input/errors.
        code = str(error) if isinstance(error, ValidationError) else type(error).__name__
        print("FAIL: " + code, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
