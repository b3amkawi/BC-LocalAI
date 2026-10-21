## What this changes

Refs #

## Checks

- [ ] CI is green (build, .NET, JavaScript and Python tests, secret scan, doc links)
- [ ] The number of tests did not drop, or the drop is explained below and `docs/devops/test-baseline.json` is updated in this PR
- [ ] No real secret, password, token or connection string in code, docs or logs

## If this PR touches AI work (Ollama, prompts, datasets, benchmarks)

CI cannot run the live suites. Paste the evidence block from `scripts/ci/new-live-evidence.ps1`
(machine, Ollama version, model digest, SHA-256 of the dataset and scripts, result file):

```
<evidence block>
```

See `docs/devops/live-test-procedure.md`.

## Notes for the reviewer
