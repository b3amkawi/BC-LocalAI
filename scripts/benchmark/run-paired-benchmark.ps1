<#
.SYNOPSIS
AI-006: runs the AI-004 benchmark with Direct (Ollama) and Backend requests ALTERNATING inside every scenario.
.DESCRIPTION
Requires PowerShell 7, Ollama and (unless -SkipBackend) the Backend running. Loopback only.

AI-004 ran Direct and Backend as two separate long runs, so the Backend overhead could not be told apart
from drift between the runs. Here each scenario (profile x concurrency) is cut into slices; slice k runs
Direct then Backend (and Backend then Direct on the next slice), so both targets see the same machine
state. Slices reuse ollama-benchmark.ps1 and backend-benchmark.ps1 unchanged, append to one CSV, and write
one summary JSON per slice (wall time and resource use). The script resumes: scenarios whose targets already
have -MinSamples measured requests in the CSV are skipped, so an interrupted run can be started again.

-SkipBackend runs the Direct target only (used for the OLLAMA_NUM_PARALLEL comparison, where the Backend is
not involved). Nothing is changed on Ollama or the Backend.
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\run-paired-benchmark.ps1 -OutputDir docs\performance\post-be003 -MinSamples 188
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDir,
    [uri]$OllamaUrl = 'http://127.0.0.1:11434',
    [uri]$BackendUrl = 'http://localhost:5290',
    [string[]]$Concurrency = @('1', '2', '5', '10'),
    [string[]]$Profiles = @('short', 'medium', 'long'),
    [ValidateRange(1, 2000)][int]$MinSamples = 188,
    [ValidateRange(2, 200)][int]$SliceRequests = 40,
    [ValidateRange(1, 1800)][int]$TimeoutSec = 300,
    [ValidateRange(0, 1048576)][int]$MinAvailableMemoryMB = 512,
    [switch]$SkipBackend,
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BenchmarkCommon.psm1') -Force
Assert-LoopbackBaseUrl $OllamaUrl
if (-not $SkipBackend) { Assert-LoopbackBaseUrl $BackendUrl -RejectOllamaPort }
$Concurrency = ConvertTo-BenchmarkList $Concurrency -Kind int
$Profiles = ConvertTo-BenchmarkList $Profiles -Kind profile

$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$slicesDir = Join-Path $out 'slices'
$null = New-Item -ItemType Directory -Force -Path $out, $slicesDir
$csv = Join-Path $out 'benchmark-results.csv'
$pwsh = (Get-Process -Id $PID).Path
$targets = if ($SkipBackend) { @('ollama') } else { @('ollama', 'backend') }
$scriptFor = @{ ollama = 'ollama-benchmark.ps1'; backend = 'backend-benchmark.ps1' }

function Get-MeasuredCount([string]$Target, [string]$Profile, [int]$Level) {
    if (-not (Test-Path -LiteralPath $csv)) { return 0 }
    $n = 0
    foreach ($row in (Import-Csv -LiteralPath $csv)) {
        if ($row.target -eq $Target -and $row.profile -eq $Profile -and [int]$row.concurrency -eq $Level -and $row.phase -eq 'measured') { $n++ }
    }
    $n
}

function Invoke-Slice([string]$Target, [string]$Profile, [int]$Level, [int]$Index, [int]$Warmup) {
    $name = '{0}-c{1}-{2:D3}-{3}' -f $Profile, $Level, $Index, $Target
    $argsList = @('-NoProfile', '-File', (Join-Path $PSScriptRoot $scriptFor[$Target]),
        '-Concurrency', "$Level", '-Profiles', $Profile, '-Rounds', '1', '-MinSamples', "$SliceRequests", '-WarmupRequests', "$Warmup",
        '-TimeoutSec', "$TimeoutSec", '-MinAvailableMemoryMB', "$MinAvailableMemoryMB",
        '-OutputCsv', $csv, '-AppendCsv', '-OutputJson', (Join-Path $slicesDir "$name.json"))
    if ($Target -eq 'ollama') { $argsList += @('-BaseUrl', $OllamaUrl.AbsoluteUri) } else { $argsList += @('-BaseUrl', $BackendUrl.AbsoluteUri) }
    $output = & $pwsh @argsList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Slice $name failed (exit $LASTEXITCODE): $($output | Select-Object -Last 3 | Out-String)" }
}

$started = Get-Date
$plan = foreach ($p in $Profiles) { foreach ($c in $Concurrency) { [pscustomobject]@{ Profile = $p; Level = $c } } }
$done = 0
foreach ($sc in $plan) {
    $done++
    $have = @{}
    foreach ($t in $targets) { $have[$t] = Get-MeasuredCount $t $sc.Profile $sc.Level }
    $perSlice = [int][math]::Ceiling($SliceRequests / $sc.Level) * $sc.Level
    $slicesNeeded = [int][math]::Ceiling($MinSamples / $perSlice)
    $slicesHave = [int][math]::Floor(($targets | ForEach-Object { $have[$_] } | Measure-Object -Minimum).Minimum / $perSlice)
    if ($slicesHave -ge $slicesNeeded) { Write-Output ("[{0}/{1}] {2} c={3}: already complete ({4} slices)" -f $done, $plan.Count, $sc.Profile, $sc.Level, $slicesHave); continue }
    Write-Output ("[{0}/{1}] {2} c={3}: slices {4}..{5} of {5} ({6} requests per slice and target) {7}" -f $done, $plan.Count, $sc.Profile, $sc.Level, ($slicesHave + 1), $slicesNeeded, $perSlice, (Get-Date).ToString('HH:mm:ss'))
    for ($k = $slicesHave + 1; $k -le $slicesNeeded; $k++) {
        $order = if ($k % 2 -eq 1) { $targets } else { @($targets)[-1..-(@($targets).Count)] }   # alternate who goes first
        $warm = if ($k -eq 1) { 1 } else { 0 }
        foreach ($t in $order) { Invoke-Slice $t $sc.Profile $sc.Level $k $warm }
    }
}

$meta = [ordered]@{
    label = $Label; started = $started.ToString('o'); finished = (Get-Date).ToString('o'); min_samples_per_scenario_and_target = $MinSamples
    slice_requests = $SliceRequests; targets = $targets; concurrency = $Concurrency; profiles = $Profiles
    ollama_url = $OllamaUrl.AbsoluteUri; backend_url = $(if ($SkipBackend) { $null } else { $BackendUrl.AbsoluteUri })
}
[IO.File]::WriteAllText((Join-Path $out 'run-meta.json'), ($meta | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output ("Finished in {0:N0} minutes. Results: {1}" -f ((Get-Date) - $started).TotalMinutes, $csv)
