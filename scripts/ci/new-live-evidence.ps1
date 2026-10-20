#requires -Version 7.0
<#
.SYNOPSIS
  Prints the evidence block to paste in a pull request that touches AI work.

.DESCRIPTION
  CI cannot run the live suites (they need Ollama and a GPU). Run the suite on a machine that
  has them, then run this script to record what was used: machine, Ollama version, model digest,
  and SHA-256 of the dataset, scripts and result files. Reads only; changes nothing and sends
  nothing. Ollama is queried on loopback only.

.EXAMPLE
  pwsh -File scripts/ci/new-live-evidence.ps1 -Files docs/ai/chat-evaluation-cases.json,scripts/test-chat-e2e.ps1,docs/ai/chat-evaluation-evidence.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Files,
    [string]$Model = 'qwen3:4b',
    [string]$OllamaUrl = 'http://127.0.0.1:11434'
)
$ErrorActionPreference = 'Stop'

if ([Uri]$OllamaUrl -and -not ([Uri]$OllamaUrl).IsLoopback) {
    throw "Refusing to query a non-loopback Ollama address: $OllamaUrl"
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("Date (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))")
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { $lines.Add("OS: $($os.Caption) $($os.Version)") }
$gpu = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>$null
if ($gpu) { $lines.Add("GPU: $gpu") } else { $lines.Add('GPU: not detected') }

try {
    $version = (Invoke-RestMethod -Uri "$OllamaUrl/api/version" -TimeoutSec 5).version
    $lines.Add("Ollama: $version")
    $tags = Invoke-RestMethod -Uri "$OllamaUrl/api/tags" -TimeoutSec 5
    $entry = $tags.models | Where-Object { $_.name -eq $Model } | Select-Object -First 1
    if ($entry) { $lines.Add("Model: $Model digest $($entry.digest.Substring(0, [Math]::Min(12, $entry.digest.Length)))") }
    else { $lines.Add("Model: $Model NOT FOUND in /api/tags") }
} catch {
    $lines.Add('Ollama: not reachable (the live suite cannot have run on this machine)')
}

$lines.Add('SHA-256:')
foreach ($f in $Files) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "File not found: $f" }
    $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add("  $hash  $($f -replace '\\','/')")
}
$lines -join "`n"
