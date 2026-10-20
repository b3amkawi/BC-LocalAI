# AI-002: num_predict 1024 live smoke test

Tested on 20 October 2026 at 15:23 (Asia/Bangkok) using PowerShell 7.6.5, Ollama 0.40.5 and qwen3:4b against POST http://127.0.0.1:11434/api/generate.

The included test-ollama-1024.ps1 is a copy of scripts/test-ollama.ps1 with only num_predict = 256 changed to num_predict = 1024. The original script SHA-256 was D0687A4C2DDB51201E11CBFD44F565DC76D632B967C0E359E0E29E49432F42F7. Request bodies and complete API responses are retained in the JSON files.

| Case | Evidence | Time | Generated tokens | Exit code |
| --- | --- | ---: | ---: | ---: |
| Thai greeting | [thai.json](thai.json) | 966 ms | 26 | 0 |
| Capital of Thailand, English | [english.json](english.json) | 242 ms | 11 | 0 |
| One plus one, Thai | [math.json](math.json) | 265 ms | 7 | 0 |

All three returned HTTP 200, model qwen3:4b, done=true and done_reason=stop. Each inner response passed the existing strict JSON validation: exactly one non-empty answer string and no thinking markers. All recorded requests contain options.num_predict=1024.

This confirms successful short structured responses with a configured generation limit of 1024. It does not demonstrate generation of 1024 tokens, long-answer behavior, truncation behavior at that limit, Backend end-to-end behavior, or a performance improvement. No model unload was controlled, so these timings are not verified cold-start measurements. Existing AI-002 delivery evidence and the default contract value of 256 remain unchanged.

## Reproduce

Run in this directory with Ollama and qwen3:4b already available:

```powershell
pwsh -NoProfile -File ./test-ollama-1024.ps1 -Prompt 'สวัสดี กรุณาตอบสั้น ๆ' -OutputPath ./thai-rerun.json
pwsh -NoProfile -File ./test-ollama-1024.ps1 -Prompt 'What is the capital of Thailand? Answer briefly.' -OutputPath ./english-rerun.json
pwsh -NoProfile -File ./test-ollama-1024.ps1 -Prompt 'หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ' -OutputPath ./math-rerun.json
```

Check the exit code after each command. Reruns write separate files to preserve the committed evidence.