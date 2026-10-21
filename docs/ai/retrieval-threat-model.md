# AI-010 — Draft retrieval threat model (review pending)

This is evaluation preparation for [#43](https://github.com/b3amkawi/BC-LocalAI/issues/43),
not an approved production security design or ARCH-001 replacement.

| Boundary / threat | Required behavior | Offline coverage | Remaining live/integration work |
|---|---|---|---|
| User → retrieval; impersonated role | Identity and document grants come from authenticated server policy | Three synthetic access sets; unknown role rejected | Map approved grants to BE-006; never accept a client-supplied role |
| Document index → ranking; denied content | Filter before ranking, embeddings lookup, prompt construction and citations | Denied document cannot affect lexical scores or selected text | Verify real index partitions/ACL filtering with Backend owner |
| Document → model; instructions embedded in content | Document is untrusted evidence, never a privileged instruction or tool authorization | 10 live payloads: sources-only full-answer pass 1/10, later unit-constrained 10/10; both retained | Source/unit grammar blocks many injected strings structurally; these same-base samples do not prove general injection resistance. No tool execution in this prototype |
| Retrieval → answer; fabricated or wrong citation | Sources must be supplied and support expected answer | Whole answer and exact expected source set checked | Human support review for natural answers; quoted spans/page provenance |
| Context → runtime; silent truncation | Honor combined AI-008 budget and report omissions | Byte cap and intact units; never claims token safety | Apply token policy and verify truncation detection; don't infer correctness from prompt_eval_count alone |
| Artifacts/logs → repository; private text or credentials | Synthetic data only; public logs show counts/rule codes | Loopback-only opted-in model calls, no real accounts; CLI prints IDs/rule codes, raw artifacts contain synthetic document text | Raw capture must not be reused for company documents without redaction and data-owner review |
| Dataset → evaluator; outcome-driven grading | Freeze truth and record fingerprints before model runs | Versioned fixture generator, duplicate-key and fingerprint checks | Hold out a separate natural-question set; log every dataset amendment |
| Permission update → stored chunks/cache | Revoked documents must not remain available through caches | No answer cache or persistent vector index in prototype | Define invalidation and retention in approved architecture |

Fixture canaries are literal synthetic labels, not secrets. A transport-boundary
test passing is not evidence of model injection resistance. Similarity scores
are not permission decisions and never override the ACL filter.

Review requests for เติ้ล/Leader: ownership of document ACLs, grant inheritance,
revocation rules, storage/index access, source confidentiality, and whether
embedding content may be retained. These remain pending; no messages or requests
have been sent to other people by this preparation task.
