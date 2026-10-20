<#
.SYNOPSIS
AI-004: benchmarks Ollama /api/generate directly with the same request body as the Backend.
.DESCRIPTION
Requires PowerShell 7. Loopback only. Sends predefined short/medium/long prompts at each
concurrency level for the given number of rounds, after warm-up requests, and writes one CSV
row per request plus a JSON summary (latency percentiles, throughput, success/timeout rate,
tokens/s, CPU/RAM/GPU/VRAM). The request body mirrors the AI-002 contract used by
OllamaAIService (system prompt, JSON schema, think=false, temperature=0, num_predict=256),
so results are comparable with backend-benchmark.ps1.

Exit 0 when the run finished and results were written (failed requests are data, not a script
failure). Exit 1 for invalid input, an unreachable Ollama, or a safety stop (low RAM).
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\ollama-benchmark.ps1
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\ollama-benchmark.ps1 -Concurrency 1,2 -Profiles short -Rounds 2
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://127.0.0.1:11434',
    [string[]]$Concurrency = @('1', '2', '5', '10'),
    [string[]]$Profiles = @('short', 'medium', 'long'),
    [ValidateRange(1, 100)][int]$Rounds = 5,
    [ValidateRange(0, 20)][int]$WarmupRequests = 3,
    [ValidateRange(1, 1800)][int]$TimeoutSec = 300,
    [ValidateRange(0, 60000)][int]$ResourceIntervalMs = 1000,
    [ValidateRange(0, 1048576)][int]$MinAvailableMemoryMB = 1024,
    [string]$ColdStartNote = '',
    [string]$OutputCsv = (Join-Path $PSScriptRoot '..\..\docs\performance\benchmark-results.csv'),
    [switch]$AppendCsv,
    [string]$OutputJson = (Join-Path $PSScriptRoot '..\..\docs\performance\benchmark-summary-ollama.json')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BenchmarkCommon.psm1') -Force

$model = 'qwen3:4b'
# Mirrors BC-LocalAI/Models/Ollama/OllamaGenerateRequest.cs (AI-002 contract); a unit test guards drift.
$systemPrompt = 'Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.'
$numPredict = 256

try {
    Assert-LoopbackBaseUrl $BaseUrl
    $Concurrency = ConvertTo-BenchmarkList $Concurrency -Kind int
    $Profiles = ConvertTo-BenchmarkList $Profiles -Kind profile
    $versionClient = [Net.Http.HttpClient]::new()
    $versionClient.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        $version = ($versionClient.GetStringAsync([uri]::new($BaseUrl, '/api/version')).GetAwaiter().GetResult() | ConvertFrom-Json).version
        $tags = $versionClient.GetStringAsync([uri]::new($BaseUrl, '/api/tags')).GetAwaiter().GetResult() | ConvertFrom-Json
    } finally { $versionClient.Dispose() }
    if ($version -isnot [string] -or [string]::IsNullOrWhiteSpace($version)) { throw 'GET /api/version did not return an Ollama version.' }
    $installed = @($tags.models | Where-Object name -ceq $model)
    if (-not $installed.Count) { throw "Model $model is not installed (ollama pull $model)." }
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}

$environment = Get-HostEnvironment
$environment.ollama_version = $version
$environment.model = $model
$environment.model_digest = $installed[0].digest
$environment.model_details = $installed[0].details
$environment.request_options = [ordered]@{ think = $false; stream = $false; temperature = 0; num_predict = $numPredict; format = 'answer JSON schema' }

$newBody = {
    param([string]$Prompt)
    @{
        model = $model; prompt = $Prompt; stream = $false; think = $false; system = $systemPrompt
        format = @{ type = 'object'; properties = @{ answer = @{ type = 'string' } }; required = @('answer'); additionalProperties = $false }
        options = @{ temperature = 0; num_predict = $numPredict }
    } | ConvertTo-Json -Depth 8 -Compress
}.GetNewClosure()

try {
    $run = Invoke-ChatBenchmark -Target ollama -Endpoint ([uri]::new($BaseUrl, '/api/generate')) -NewBody $newBody -Model $model `
        -Concurrency $Concurrency -Profiles $Profiles -Rounds $Rounds -WarmupRequests $WarmupRequests -TimeoutSec $TimeoutSec `
        -ResourceIntervalMs $ResourceIntervalMs -MinAvailableMemoryMB $MinAvailableMemoryMB -ColdStartNote $ColdStartNote `
        -OutputCsv $OutputCsv -AppendCsv:$AppendCsv -OutputJson $OutputJson -Environment $environment
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}
if ($run.Aborted) { [Console]::Error.WriteLine("STOPPED: $($run.Aborted)"); exit 1 }
exit 0
