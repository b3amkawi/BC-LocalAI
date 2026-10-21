# Existing AI Benchmark Resource-Sampling Regression

2026-10-21 · discovered during BE-005 verification · user-authorized narrow fix, not an AI-002 contract change.

## Before / impact

Full Python regression: 141 tests, 1 failure /exit1, 193.812s.
`test_resource_samples_are_attached_to_scenarios` in `tests/test_benchmark_scripts.py` expected resource_sample_count >0;
actual0. It failed again in isolation. Benchmark source/tests were unchanged from pulled main when discovered.

Impact: a short scenario can report empty CPU/RAM/GPU summaries although the final output has resource samples.
This is a benchmark-reporting defect, not evidence that BE-005 Login or password verification failed.
Severity: reporting correctness, medium; not a validated authentication vulnerability.

The user initially requested a separate report/no fix, then explicitly requested fixing the problems found.
Only the reporting race, test resource cleanup and regression protection are changed; no model configuration/contract, measurement window,
load-generation, latency/throughput formula, resource safety threshold or performance baseline artifact is changed.

## Root cause and correction

`Start-ResourceSampler` timestamps before collection, enqueues after collection. Scenario summary used an early queue snapshot.
`Stop-ResourceSampler` drains a later sample, but the old code saved final samples without reattaching them to scenarios.

The deterministic test fixture replaces Start/Stop only in a child PowerShell process: an old sample exists before measurement;
an in-window sample is published only on Stop; a future sample must also be excluded. Before correction the test failed0 !=1,
even though all3 samples existed in final output. This proves the publication-order defect without relying on CIM/GPU speed.

`Get-ScenarioResourceSummary` retains the existing ±1-second request window/statistics. After stopping the sampler,
recompute only resource fields for each scenario from final samples and its original rows.
Request counts, exact wall time, latency/throughput/token calculations and output field names remain unchanged.
No fabricated fallback sample and no enlarged window is used to turn an empty summary into a pass.

Changed files: `scripts/benchmark/BenchmarkCommon.psm1`, `tests/test_benchmark_scripts.py`,
`tests/fixtures/benchmark-delayed-sampler.ps1`, `tests/test_ci_scripts.py`; test-count baseline reflects the two new cases.

The first post-fix full run passed142 tests in201.401s but retained ResourceWarnings. The benchmark test helpers opened CSVs
without context managers, and the CI live-evidence mock server shutdown did not close its listening socket.
CSV helpers now close reads; server teardown closes the socket in finally and joins its serving thread.
The new cleanup regression failed before correction with an open descriptor instead of -1, then passed after correction.
These are test-harness lifecycle fixes only; no warning is filtered or ignored to claim clean verification.

## Actual verification

- New deterministic regression before fix: 1 failure /exit1, count0 instead of1.
- After fix: same test passed1/1, validating all8 resource fields and excluding old/future samples.
- Original failing hardware-sampler case passed three consecutive isolated runs.
- Final full Python result: recorded in the [BE-005 final verification](authentication/BE-005-LOCKOUT-VERIFICATION.md).
- First full post-sampler-fix run:142 tests passed, existing CSV/socket ResourceWarnings exposed and then corrected as described above.
- Final full run after cleanup:143 tests passed /0 failed /0 skipped, 205.865s /exit0; no ResourceWarning in the final log.
- No real Ollama benchmark, no baseline performance numbers regenerated, no team AI branch modified.
