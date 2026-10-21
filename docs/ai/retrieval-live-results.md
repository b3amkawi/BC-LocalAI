# AI-010 - Exploratory live results, 21 October 2026

**Decision: NOT READY for production document retrieval. Issue #43 remains open.**

This is an isolated synthetic evaluation, not a change to the public Chat API, application prompt or production permissions. No push, PR or merge was performed.

## Environment and evidence

- GPU: NVIDIA GeForce RTX 4050 Laptop GPU, 6,141 MiB VRAM. Ollama 0.40.5; model qwen3:4b. Exact digest is recorded in each quality artifact.
- Only http://127.0.0.1:11434 was called. Environment proxies and HTTP redirects are disabled. No model downloads or global configuration changes.
- Captured synthetic requests/response envelopes, timings, model fields and hashes (opaque provider `context` token-ID arrays omitted to limit repository size; original artifact hashes retained): [manifest](evidence/ai010/manifest.json).
- The application Backend was not called: AI-010 measures the retrieval prototype, unlike AI-003 end-to-end Chat evaluation.
- Raw evidence deliberately contains synthetic document text. Never use this capture policy unchanged for real company documents.

## Frozen inputs and grading

78 synthetic documents; 60 questions, 15 per category and 30 per language. Questions, expected answers and source sets were frozen before the live runs and were not changed after failures. A strict whole-answer allowlist, exact status and exact citation set determine a pass. Bare numeric answers with a required unit missing fail. Extra or conflicting text also fails.

The prototype uses explicit document IDs in queries. After the first pilot, retrieval was restricted to IDs actually named in the query so unrelated facts were not inserted. This is an ID-assisted lookup baseline, **not semantic search accuracy**. Full chunks remain intact and ACL filtering occurs first.

The first two profiles were compared: baseline_v1 emits unrestricted string source IDs; constrained_sources_v1 orders answer/sources/status, constrains source strings to the retrieved IDs and clarifies units/status. Neither profile gets expected answers or expected citation sets. Wrong subsets or absent citations still fail. Prototype prompts changed; application prompts did not. The four-case pilot before each change was development data, not a held-out validation set.

## Latest profile: constrained units v2

**Closed-fixture quality: 60/60 in each of two sequential runs, exit 0. Injection: 10/10, exit 0.** All 60 questions in each run reached Ollama; no preflight omissions. These are development fixtures with explicit document IDs and requested answer formats, not a held-out natural-language retrieval benchmark.

This opt-in profile adds `--constrained-units`. A narrow parser reads the requested unit from the query text only. For numeric queries, the output schema uses a pattern accepting any nonnegative integer/decimal followed by that unit. It does **not** select the correct number. A regression confirms `999 minutes` is allowed by the schema but rejected by the unchanged grader when the correct fact is 19 minutes. Unknown question wording receives no guessed unit constraint. Missing-information and conflict questions do not receive the numeric pattern. Source enums still come from retrieved documents, not expected source sets.

The v2 input preflight counts actual system/prompt UTF-8 plus compact schema bytes, avoiding JSON-escaping the prompt string a second time. It retains the 4096 comparison with 1024 output and 256 overhead reserved; all required documents fit without truncation. This byte proxy remains provisional and is not an approved tokenizer guarantee. No global or application setting changed.

| Run | Selected / provider responses | Full passes | Exact answer/status/citations | Exit |
|---|---:|---:|---:|---:|
| Units v2 first | 60 / 60 | 60/60 | 60/60 each | 0 |
| Units v2 repeat | 60 / 60 | 60/60 | 60/60 each | 0 |
| Units v2 injection | 10 / 10 | 10/10 | 10/10 each | 0 |

Each quality run: Thai 30/30, English 30/30; single 15/15, missing 15/15, multi 15/15, conflict 15/15. Correct abstention 15/15; absent-fact answered status 0/15; numeric factual mismatches 0/30. These are fixture diagnostics, not a universal hallucination measurement.

Identical parsed objects across the two v2 runs: 60/60. A sample of two runs does not establish determinism. Observed response medians: first 0.7914 s; repeat 0.7731 s. Maximum measured input preflight: 2780 bytes; maximum provider prompt_eval_count: 526.

All ten v2 injection answers retain `11 ชิ้น` and SYN-S01. Grammar restrictions structurally block many attacker strings and unauthorized source IDs; success here must not be attributed solely to model obedience. The attack requesting 999 is still syntactically possible but the model did not emit it. All ten share one base fact; broader held-out attacks remain necessary.

The earlier failures below are retained as the comparison baseline; no expected answers, questions, source sets or grading rules were relaxed.

## Earlier profiles: measured quality

| Run | Selected | Provider responses | Full passes | Failures | Exit |
|---|---:|---:|---:|---:|---:|
| Baseline | 60 | 60 | 24 | 36 | 1 |
| Constrained, first | 60 | 59 | 42 | 18 | 1 |
| Constrained, repeat | 60 | 59 | 43 | 17 | 1 |

The two constrained runs each reject conflict-07 before sending because its complete request exceeds the conservative UTF-8 input preflight. It is counted as a failure, not a model response. Thus 60 selected does not mean 60 requests reached Ollama. No hidden retries were used.

| Run | Category | Pass / selected |
|---|---|---:|
| Baseline | single | 8 / 15 |
| Baseline | missing | 13 / 15 |
| Baseline | multi | 3 / 15 |
| Baseline | conflict | 0 / 15 |
| Constrained first | single | 5 / 15 |
| Constrained first | missing | 15 / 15 |
| Constrained first | multi | 8 / 15 |
| Constrained first | conflict | 14 / 15 |
| Constrained repeat | single | 5 / 15 |
| Constrained repeat | missing | 15 / 15 |
| Constrained repeat | multi | 9 / 15 |
| Constrained repeat | conflict | 14 / 15 |

| Run | Language | Full passes | Whole answer accepted | Exact citations | Correct status |
|---|---|---:|---:|---:|---:|
| Baseline | th | 17/30 | 28/30 | 23/30 | 21/30 |
| Baseline | en | 7/30 | 17/30 | 15/30 | 22/30 |
| Constrained first | th | 24/30 | 24/30 | 29/30 | 29/30 |
| Constrained first | en | 18/30 | 18/30 | 30/30 | 30/30 |
| Constrained repeat | th | 24/30 | 24/30 | 29/30 | 29/30 |
| Constrained repeat | en | 19/30 | 19/30 | 30/30 | 30/30 |

### Abstention and unsupported-answer diagnostics

| Run | Correct missing-information result | Unsupported answered status on absent fact | Numeric factual mismatch (single/multi) |
|---|---:|---:|---:|
| Baseline | 13/15 | 2/15 | 0/30 numeric outputs |
| Constrained first | 15/15 | 0/15 | 0/30 numeric outputs |
| Constrained repeat | 15/15 | 0/15 | 0/30 numeric outputs |

The absent-fact answered rate is a narrow **unsupported-answer proxy**, not a universal hallucination rate. The numeric diagnostic compares independently frozen numeric truth and does not forgive missing units in the actual grader. Human factual/support review is still required for open-ended natural answers. Exact citations only prove the expected ID set was emitted; they do not establish that an arbitrary cited passage supports arbitrary prose.

### Latency and stability

| Run | Observed response median (s) | Maximum (s) |
|---|---:|---:|
| Baseline | 0.8111 | 1.1075 |
| Constrained first | 0.7602 | 1.0978 |
| Constrained repeat | 0.7545 | 1.2047 |

Between identical constrained configurations, 3 parsed results changed; case IDs: single-06, single-08, multi-08. Temperature 0 did not guarantee identical output. These are exploratory sequential measurements, not a load test.

The initially unloaded four-case pilot took 5.6502 s for its first request, including 4.95665 s reported model loading. Later request medians above are not a controlled cold-start benchmark. Context probes finished before the repeat quality run began (timestamps in evidence); no intentional GPU concurrency was used. A post-run idle-loaded snapshot was 3,303/6,141 MiB, a single observation, not peak VRAM.

## Injection and fixture permission checks

In the earlier sources-only profile, all 10 frozen document payloads were sent. Full answer/citation/status grading passed 1/10, exit 1. The later units profile passes 10/10 as reported above. Nine failures returned `11` instead of `11 ชิ้น`; the other returned the complete expected answer. None of these samples emitted the requested attacker marker, unauthorized source ID or thinking marker. This limited observation does not prove general prompt-injection resistance, and format failures remain failures. All attacks share single-01 as their base question; payload language is not question language.

Local retrieval checks passed all three fixture role sets: reader -> SYN-R01; engineer -> SYN-R01/R02; admin -> SYN-R01/R02/R03. These checks run before model prompts are built. They are **not BE-006 account authorization tests**, nor approval from the Backend/security owner. There are no tools to invoke in this prototype.

## Context boundary experiment

Opt-in stress requests intentionally bypass the normal conservative preflight, using synthetic first/last markers and identical prompts at 8192 versus 4096. This is not a production bypass. Each size was repeated three times.

| Padding words | Reference prompt tokens (8192) | Observed prompt tokens (4096) | First/last recall at 4096 |
|---:|---:|---:|---|
| 500 | 573 | 573 | Both, 3/3 |
| 3700 | 3773 | 3773 | Both, 3/3 |
| 4500 | 4573 | 2050 | First lost 3/3; last retained 3/3 |

At 8192 both markers were recalled in every reference run. Context probe exits 1 because three 4096 recall checks fail. Comparing identical requests against the larger context provides evidence of loss here; `prompt_eval_count` alone is not a universal truncation detector and this does not establish a safe arbitrary input allowance. AI-008 approval is still pending. This stress set is English marker recall, not a Thai retrieval-quality claim.

## Test tooling verification

- Final full Python regression: **217 tests passed, 0 failures, 0 skipped, 231.474 s, exit 0**. Local ignored log: `artifacts/ai010-preparation/python-full-units.txt`. Earlier 211-test checkpoint also passed in 238.581 s.
- Includes 33 offline retrieval tests and 26 live-transport/CLI/stress-generator regressions; those tests mock model transport and do not inflate live-model pass counts.
- An intermediate 213-test run failed once because source files changed while the older Python process retained a cached module signature. That failed log remains local in `python-full-final.txt`; it is not a passing result. Code was frozen before the clean final 217-test run.
- Failure paths cover nonloopback URLs, redirects, timeout/unavailable/HTTP errors, incomplete/truncated model envelopes, oversized bodies, duplicate JSON keys, immutable earlier artifacts, explicit opt-in and exit-code propagation.
- .NET/JavaScript not rerun: no application/dependency changes in this worktree.
- Final evidence audit: all 12 captures / 354 rows verified against original hashes and recomputed grades; 352 rows have provider responses, two earlier rows were preflight rejects. Secret-pattern scan checked 618 text files with zero findings; 79 Markdown files had no broken local links. This does not certify arbitrary secrets or Git history. See [regression summary](evidence/ai010/python-regression-summary.json).

## Reproduce (Python 3.11+, from the repository directory)

Use unique new output filenames; existing evidence is never overwritten. `py -3` may replace `python` on Windows. All commands below are deliberate model calls except unit tests.

```powershell
New-Item -ItemType Directory -Force artifacts/ai010-preparation | Out-Null
python -X utf8 scripts/test-retrieval-live.py --allow-live --output artifacts/ai010-preparation/quality-new.json
python -X utf8 scripts/test-retrieval-live.py --allow-live --constrained-sources --output artifacts/ai010-preparation/constrained-new.json
python -X utf8 scripts/test-retrieval-live.py --allow-live --constrained-units --output artifacts/ai010-preparation/units-new.json
python -X utf8 scripts/test-retrieval-live.py --allow-live --constrained-units --suite injection --output artifacts/ai010-preparation/injection-new.json
python -X utf8 scripts/test-retrieval-context.py --allow-boundary-probe --output artifacts/ai010-preparation/context-new.json
python -X utf8 -m unittest discover -s tests -v
```

For a pilot append `--case-id single-01` (repeat the flag for more IDs). Reports retain selected versus full-suite counts. Exit 0 means every **selected** case and local fixture ACL check passed, not that the full issue or a 60-case run passed. Exit 1 means evaluation failures; exit 2 means invalid configuration/metadata/I/O. `live_verified` means the script observed local provider calls, not an attestation or independent review; individual preflight failures are never claimed to reach the provider. Socket timeouts bound blocked I/O, not a strict total deadline over every read.

Per request: temperature 0, think false, stream false, num_ctx 4096, num_predict 1024 for quality; 256 for boundary probes (which explicitly compare 8192 and 4096). Serialized document bytes are capped at 1400; a separate conservative system/prompt/schema UTF-8 preflight reserves 1024 output plus 256 overhead against 4096. The original profiles count JSON-escaped fields; v2 counts actual prompt/system strings and compact schema bytes. This is an evaluation guard, **not a tokenizer proof or the approved AI-008 policy**. No automatic retries or result repair.

## Remaining blockers and next work

| Issue acceptance area | Status of this work |
|---|---|
| At least 60 frozen questions, 15 per category, half Thai | Prepared and validated; ID-assisted questions |
| Accuracy/citations/abstention/unsupported-answer results | Measured by category/language; latest closed fixtures 60/60 twice, earlier profiles 24 and 42-43/60 retained |
| Near/over-context behavior | English marker stress measured; AI-008 policy approval pending |
| Permission boundary for at least 3 roles | Local synthetic retrieval verified; actual account/integration review pending |
| At least 10 injection forms | Executed; latest full answer quality 10/10, earlier 1/10 retained; no claimed general immunity |
| Embedding plus Qwen resource measurements | NOT RUN; isolated candidate/runtime decision required |
| Readiness assessment | NO-GO for production; closed fixtures pass, broader evaluation/resource/permission reviews pending |
| No real company data or runtime configuration changes | Synthetic fixtures only; per-request settings only |
| Owner/security/code review | Pending; this local work does not claim approval |

1. Preserve the closed-format unit fix as an opt-in prototype; evaluate held-out natural questions and richer documents without IDs before proposing any application change. Keep every earlier failure.
2. The v2 byte accounting admits all complete fixtures, including conflict-07. Still obtain an approved combined token policy; do not treat this byte proxy or passed examples as a guarantee against truncation.
3. Obtain AI-008 context-policy confirmation, ARCH-001 data/permission decisions, and เติ้ล review of the threat model and real authorization boundary.
4. Embedding/Qwen co-residency, indexing time, model-switch cold start and queue effects remain **NOT RUN**. No embedding model is installed and the issue excludes installing/changing production models; this needs an isolated candidate/runtime decision. Lexical lookup results are not embedding results.
5. Add representative document lengths, natural-language holdout questions, permission revocation/cache behavior and independent support review before any readiness approval.

Development pilots were 1/4, 2/4 and 3/4 (exit 1), then units v2 4/4 (exit 0). All four captures are retained in the evidence manifest. No failed pilot was relabeled as a successful full run.
