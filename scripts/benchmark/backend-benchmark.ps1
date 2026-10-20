<#
.SYNOPSIS
AI-004: benchmarks the Backend POST /api/chat with the same scenarios as ollama-benchmark.ps1.
.DESCRIPTION
Requires PowerShell 7. Calls only the loopback Backend (never the Ollama port). Sends the
same predefined short/medium/long prompts at each concurrency level and writes one CSV row
per request plus a JSON summary. HTTP 504 AI_TIMEOUT is recorded as a server-side timeout;
a request that exceeds -TimeoutSec on the client is a client timeout. Keep -TimeoutSec above
the Backend Ollama:TimeoutSeconds so the two cannot be confused.

Exit 0 when the run finished and results were written (failed requests are data, not a script
failure). Exit 1 for invalid input or a safety stop (low RAM).
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\backend-benchmark.ps1 -AppendCsv
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://localhost:5290',
    [string[]]$Concurrency = @('1', '2', '5', '10'),
    [string[]]$Profiles = @('short', 'medium', 'long'),
    [ValidateRange(1, 100)][int]$Rounds = 5,
    [ValidateRange(0, 20)][int]$WarmupRequests = 3,
    [ValidateRange(1, 1800)][int]$TimeoutSec = 300,
    [ValidateRange(0, 60000)][int]$ResourceIntervalMs = 1000,
    [ValidateRange(0, 1048576)][int]$MinAvailableMemoryMB = 1024,
    [string]$ColdStartNote = '',
    [string]$SettingsPath = (Join-Path $PSScriptRoot '..\..\BC-LocalAI\appsettings.json'),
    [string]$OutputCsv = (Join-Path $PSScriptRoot '..\..\docs\performance\benchmark-results.csv'),
    [switch]$AppendCsv,
    [string]$OutputJson = (Join-Path $PSScriptRoot '..\..\docs\performance\benchmark-summary-backend.json')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BenchmarkCommon.psm1') -Force

try {
    Assert-LoopbackBaseUrl $BaseUrl -RejectOllamaPort
    $Concurrency = ConvertTo-BenchmarkList $Concurrency -Kind int
    $Profiles = ConvertTo-BenchmarkList $Profiles -Kind profile
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}

$environment = Get-HostEnvironment
$environment.backend_endpoint = [uri]::new($BaseUrl, '/api/chat').AbsoluteUri
# Record the configured Backend timeout/model from the settings file (read only, never changed).
if (Test-Path -LiteralPath $SettingsPath) {
    try {
        $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
        $environment.backend_ollama_timeout_seconds = $settings.Ollama.TimeoutSeconds
        $environment.backend_ollama_model = $settings.Ollama.Model
    } catch { $environment.backend_settings_error = 'settings file is not valid JSON' }
}

$newBody = { param([string]$Prompt) @{ message = $Prompt } | ConvertTo-Json -Compress }

try {
    $run = Invoke-ChatBenchmark -Target backend -Endpoint ([uri]::new($BaseUrl, '/api/chat')) -NewBody $newBody `
        -Concurrency $Concurrency -Profiles $Profiles -Rounds $Rounds -WarmupRequests $WarmupRequests -TimeoutSec $TimeoutSec `
        -ResourceIntervalMs $ResourceIntervalMs -MinAvailableMemoryMB $MinAvailableMemoryMB -ColdStartNote $ColdStartNote `
        -OutputCsv $OutputCsv -AppendCsv:$AppendCsv -OutputJson $OutputJson -Environment $environment
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}
if ($run.Aborted) { [Console]::Error.WriteLine("STOPPED: $($run.Aborted)"); exit 1 }
exit 0
