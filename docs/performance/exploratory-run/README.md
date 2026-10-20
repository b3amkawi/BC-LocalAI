# Exploratory run (superseded by the final run)

The first complete pass, as the Issue describes ("repeat each scenario at least 5 times in the exploratory round"): 5 rounds per scenario (4 to 50 samples), 5 prompts per profile, 4 concurrency levels, Direct and Backend.

It is kept because it was the first full data set and its conclusions agree with the final run (Ollama serialises requests, Thai long answers hit `num_predict=256`, no timeouts). It is not the basis of the report: the sample sizes are too small for P99, the sampler counted only the Ollama server process and read free instead of available memory, and the `queue_wait_ms` column was recomputed afterwards from the recorded raw durations (formula fixed in the script; the raw columns were not edited). Use `../benchmark-report.md` for results.
