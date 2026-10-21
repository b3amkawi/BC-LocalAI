# AI-010 — Offline preparation and Backend handoff

Status on 21 October 2026: **preparation only; live model evaluation NOT RUN**.
Issue: [AI-010 #43](https://github.com/b3amkawi/BC-LocalAI/issues/43).
Base: `d636b4f`, local branch `codex/ai010-preparation`.

This work can run while Claude measures AI-008: it has no network client, GPU
work, model downloads, database calls or changes to the application. It does not
change the running system prompt, AI-002 contract or Ollama configuration.

## What is ready

- 78 short synthetic documents in `tests/retrieval/documents.json`: 75 factual
  documents and 3 fixture-ACL examples. There are no real company documents,
  named employees, passwords or credentials.
- 60 frozen questions in `tests/retrieval/questions.json`: 15 single-source,
  15 missing-information, 15 multi-source sums and 15 conflicting-source cases;
  exactly 30 Thai and 30 English questions.
- 10 distinct **planned** injection payloads in
  `tests/retrieval/injection-cases.json`. These are not successful live tests.
- A standard-library lexical retrieval baseline. Documents are filtered by
  fixture ACL **before** computing frequencies, scores or prompt content.
- Whole-answer grading and exact source-set checks; duplicates, wrong sources,
  omitted sources and sources not supplied to the model fail.
- Dataset/corpus SHA-256 fingerprints on prepared artifacts and imported
  responses prevent accidentally grading against changed ground truth.
- Atomic output replacement: validation failures preserve earlier evidence.

The three fixture roles `reader`, `engineer`, `admin` simulate access sets only.
They are **not** new application roles and do not replace BE-006 authorization.
ARCH-001 and the Backend owner must approve a real document-ACL policy.

## Run from the repository folder

Use Python 3.11+; no third-party packages need installing. In Windows PowerShell,
`py -3` is an alternative when `python` is unavailable.

```powershell
py -3 -X utf8 scripts/test-retrieval.py validate
New-Item -ItemType Directory -Force artifacts/ai010-preparation | Out-Null
py -3 -X utf8 scripts/test-retrieval.py prepare --output artifacts/ai010-preparation/prepared.json
py -3 -X utf8 -m unittest discover -s tests -p test_retrieval_eval.py -v
```

PowerShell 7 convenience launcher:

```powershell
pwsh -NoProfile -File scripts/test-retrieval.ps1 -Command validate
pwsh -NoProfile -File scripts/test-retrieval.ps1 -Command prepare -OutputPath artifacts/ai010-preparation/prepared.json
```

`prepare` writes proposed requests, selected document IDs/text, byte counts and
omissions. **It does not send them.** Its proposed 1024 output-token ceiling
matches the current Backend default; the final request and input allowance still
need AI-008 confirmation. The proposed schema is an isolated evaluation format,
not a change to the public Chat API.

Exit codes: `0` command succeeded (or every imported fixture response matched),
`1` at least one imported response failed, `2` invalid input/configuration/I/O.
**Exit 0 in this offline tool does not mean AI-010 is complete.** Every artifact
contains `live_verified: false` and `issue_complete: false`.

## Response grading, for harness verification only

An importer may produce this envelope, using fingerprints copied from the
prepared artifact. Each case ID must occur once and all 60 IDs must be supplied
for a passing grading run. Missing cases fail; unknown or duplicate IDs are
input errors. `result` is a parsed object, not a JSON string in another string.

```json
{
  "dataset_sha256": "<copy from prepared artifact>",
  "corpus_sha256": "<copy from prepared artifact>",
  "responses": [
    {
      "id": "single-01",
      "result": {
        "status": "answered",
        "answer": "11 ชิ้น",
        "sources": ["SYN-S01"]
      }
    }
  ]
}
```

This one-row example intentionally cannot pass a 60-case run.

```powershell
py -3 -X utf8 scripts/test-retrieval.py grade --responses artifacts/ai010-preparation/responses.json --output artifacts/ai010-preparation/graded.json
```

Imported response grading has scope
`offline_imported_response_grading_not_live_evidence`, even if all fixtures
match. The tool has not observed how the supplied answers were produced; a file
of reference answers is not live model evidence.

## Deliberate limitations

1. This is an **identifier-assisted lexical baseline**, not a trained retriever,
   embedding benchmark or production RAG system. Questions deliberately name
   synthetic document IDs. Strong source coverage here does not establish
   retrieval quality for natural user questions without document identifiers.
2. Each short document is one retrieval unit. Real PDF parsing, page/section
   splitting and chunk overlap are not implemented. That design belongs after
   document types and evaluation policy are approved.
3. `--max-context-bytes` limits only UTF-8 bytes of the serialized documents
   array, including JSON overhead. It is **not a token limit** and excludes the
   query, system/template and output reservation. Defaults are test scaffolding,
   not a safe Ollama context budget. Oversized units are omitted, never silently
   cut; omissions are reported. AI-008 must supply the final combined allowance.
4. Exact answers reject unsupported additions/contradictions, but also reject
   valid paraphrases not on the frozen list. It is suitable for these short,
   closed-answer fixtures, not universal semantic correctness.
5. JSON separation keeps injected text in the document-data field. It does not
   prove that a model will ignore that text. All 10 attacks need real model runs.
6. A status `not_found` alone does not prove correct abstention; expected answers
   and empty citations are checked together. Conflict cases require the explicit
   conflict response and both sources, with no assumed newer/higher authority.
7. The prototype cannot establish GPU capacity, embedding co-residency, hardware
   performance or the application's authentication strength.

## Gates before real model evaluation

| Gate | Owner/dependency | Current status |
|---|---|---|
| Confirm prompt/template + history + retrieved-data + output budget | AI-008 #41 | Pending; Claude owns active work |
| Approve retrieval permissions and injection threat model | ARCH-001 / Leader + เติ้ล | Pending |
| Map fixture access sets to server-authenticated identity/ACL | BE-006 owner | Not integrated |
| Choose an embedding candidate and approve isolated model trial | Leader + AI owner | Not selected/downloaded here |
| Reserve GPU time after AI-008; pin runtime/model digests | AI owner | No live runs scheduled by this tool |
| Evaluate natural questions without IDs, languages and document lengths | AI owner + reviewer | Not measured by the 60 identifier fixtures |

Proposed live go/no-go thresholds must be signed off **before** seeing live
answers. Record numerical thresholds for answer accuracy, supported citations,
correct abstention, conflict handling and latency. Any unauthorized source in
retrieved context is a blocker. A blocked model run must be reported as blocked,
not passed or silently removed from the denominator.

## Evidence needed to finish AI-010

Keep raw request/response and retrieval provenance with configuration,
Ollama/embedding/model digests, hardware, timestamps and dataset fingerprints.
Report accuracy, citation support, abstention and unsupported answers by language
and category. Measure context near/over the AI-008 budget, injection outcomes,
and embedding + Qwen VRAM/cold-start/indexing/queue behavior. Do not promote
offline fixtures into `retrieval-evidence.json` as though Ollama produced them.

See [threat model](retrieval-threat-model.md) and
[preparation verification](retrieval-preparation-results.md).
