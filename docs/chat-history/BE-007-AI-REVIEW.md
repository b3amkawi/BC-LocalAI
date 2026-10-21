# BE-007 — AI Engineer review (qwen3:4b, real Ollama)

Reviewed branch `watzapon_BE-007` (7d5339e, includes main babbd61). Fixes are on `pao_BE-007-review`.
Evidence: [token-estimate-evidence.json](../ai/token-estimate-evidence.json), [be007-context-check.json](../ai/be007-context-check.json).
Scripts: `scripts/measure-prompt-tokens.py`, `scripts/check-be007-context.py` (loopback Ollama only).

## Findings

| # | Finding | Evidence | Fix |
|---|---------|----------|-----|
| 1 | **Estimator too low.** `4 + ceil((bytes+2)/3)` fits Thai/English but real tokens were 1.4–3.0× the estimate for digits, symbols, JSON, URLs/ids and Thai with numbers (worst: digits 2.98×). | 30 measurements (10 text kinds × 3 sizes) | Per-character estimate: digit/ASCII symbol 1, ASCII letter 0.5, other 1 per 3 UTF-8 bytes. Real count ≤ estimate on all 30 samples (digits: 1500 vs 1504). |
| 2 | **Silent truncation.** With the old estimator a history of digits at the 1500 budget was 4500 real tokens; Ollama cut the prompt to 2050 tokens and the model lost the earlier fact and answered garbage. | `overflow` in be007-context-check.json: old → 2050 tokens, 7391 not recalled; new → 1640 tokens, 7391 recalled | Finding 1 fix. |
| 3 | **Question length unbounded** (only the 1 MB storage limit); budget 1500 covers history only, so a long question overflows the 4096 context (1024 reserved for the answer). | code: no limit in `BeginAsync` | `ChatHistory:MaxQuestionTokens` (default 1200, estimator-based) → 400 `QUESTION_TOO_LONG`, nothing saved or sent. Startup check: `ContextTokenBudget + MaxQuestionTokens ≤ 2900`. |
| 4 | **Fake turns in stored text.** `Neutralize` only handled the `<conversation>` tags; a saved answer containing a line `User: …` / `Assistant: …` looked like a real turn. | read of `ConversationPrompt` | Continuation lines are indented; test added. |

## Template and recall
The template (header, `<conversation>`, `User:`/`Assistant:` lines, `Current question:`) works: fact recall 3/3 for Thai, English and mixed (port number) cases. Template and system-prompt overhead measured: 41 tokens.

## Limits that remain
- The estimate is calibrated on 10 text kinds, not proven for every text. The default budgets leave about 700 tokens of slack (4096 − 1024 − 1500 − 1200 − ~100).
- English history is over-counted ~3.4× (safe side): fewer old turns are kept than would fit. If FE/PO want longer memory for English, raise `ContextTokenBudget` and lower `MaxQuestionTokens`, keeping the sum ≤ 2900.
- Real-token check was done at the Ollama default context 4096; changing `num_ctx` needs the budgets re-derived.

## Tests
.NET 830 pass (821 + 9: estimator vs real counts ×5, digit/symbol cases, question limit API, turn-line prompt test). Python 143 run, doc-link and secret scans clean. `docs/devops/test-baseline.json` dotnet minimum raised to 830. The MySQL live tests (`tests/BE007.MySqlTests`) were not run here (needs MySQL fixture).
