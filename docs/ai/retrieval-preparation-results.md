# AI-010 — Preparation verification, 21 October 2026

**Historical offline preparation checkpoint (12:18 Bangkok).** At that checkpoint:
Ollama runs 0, embedding runs 0, GPU changes 0. Subsequent GPU work is reported
separately in [live results](retrieval-live-results.md); this page preserves the
earlier checks and does not close #43.

| Check | Actual result |
|---|---|
| Fixture generator + `test-retrieval.py validate` | 78 documents; 60 questions: 15 per category, Thai 30 / English 30 |
| `python -X utf8 -m unittest discover -s tests -p test_retrieval_eval.py -v` | 32 tests passed, including PowerShell launcher exit-code checks |
| Full Python regression `python -X utf8 -m unittest discover -s tests -v` | **190 tests passed, 0 failed, 0 skipped, 239.937 s, exit 0**; raw local ignored log `artifacts/ai010-preparation/python-full.txt` |
| Python CI minimum | Raised from 158 to 190 to include the 32 new tests; no reduction/skip waiver |
| PowerShell launcher `validate` and workspace-local `prepare` | Both exit 0; Python interpreter preferred over a present but unconfigured Windows `py` launcher |
| First focused run in sandbox | 23 passed / 2 failed: child process could not replace temp output outside workspace (`PermissionError`) |
| Same 25-test command outside sandbox, before later added tests | 25 passed; environment difference retained here |
| Workspace-local `prepare` output | Written; `scope=offline_preparation`, `live_verified=false`, `issue_complete=false` |
| Reference source coverage | All required source IDs fit in the selected units for the 60 identifier-based cases at the provisional byte cap; this is not answer accuracy |
| Whole-answer false-positive regression | Rejects `It is false that Tokyo is the capital of Japan; Paris is.` and other extra/contradictory text |
| Citations | Wrong/missing/extra/duplicate/unpresented source IDs rejected |
| Fixture permission boundary | Reader/engineer/admin synthetic sets checked; denied content does not influence ranking or prompts |
| Imported-answer grading negative control | Missing 60 responses produces exit 1; one wrong response among reference fixtures produces exit 1 |
| Imported-answer grading positive control | Reference fixtures exercise exit 0 while report still says not live; never claimed as 60 model answers |
| Ground-truth provenance | Fingerprint mismatch and duplicate/unknown response IDs rejected |
| JSON/type boundaries | Duplicate JSON keys, nonfinite constants, malformed JSON, invalid ACLs/types, oversized content and invalid Unicode rejected |

The initial lexical baseline missed one of the required sources in 14 multi-source
cases because common subject/unit terms outranked an explicit document reference.
The retriever now prioritizes exact document IDs already present in the question;
ground truth/questions were not changed to hide this. This makes the limited
identifier-assisted baseline reproducible, not a natural-language retrieval claim.

Completed portions: synthetic inputs, baseline tooling, grader regressions and
draft threat-model/handoff. Pending: AI-008 budget, ARCH-001/permission review,
natural-question holdout, real model/injection runs, embedding resource measurements,
and the final readiness decision.

No Backend/Auth/UI/DB/runtime configuration changed. No push, PR or merge performed.
Existing .NET and JavaScript suites were not rerun here because their sources and
dependencies are unchanged. Full Python regression used fixture/mock transports,
not the active Ollama instance. File/link checks cover preparation artifacts; they
do not certify future real document imports or Git history.
