<#
.SYNOPSIS
AI-004: measures cold-start latency repeatedly (model unloaded before every request).
.DESCRIPTION
Requires PowerShell 7 and the `ollama` CLI. Before each repetition it runs `ollama stop qwen3:4b`,
waits until GET /api/ps on the loopback Ollama lists no loaded model, then sends one short
request to the chosen target (Ollama /api/generate or Backend /api/chat) through the matching
benchmark script and keeps only the cold row. Stopping a loaded model is the only change made to
Ollama; no configuration is touched. Do not run it while someone else uses the same Ollama.

Exit 0 when every repetition was measured; exit 1 for invalid input, an unloadable model or any
failed request.
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\cold-start-benchmark.ps1 -Target ollama -Repeats 5
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ollama', 'backend')][string]$Target,
    [ValidateRange(1, 50)][int]$Repeats = 5,
    [uri]$BaseUrl,
    [uri]$OllamaUrl = 'http://127.0.0.1:11434',
    [ValidateRange(1, 120)][int]$UnloadWaitSec = 30,
    [string]$OutputCsv = (Join-Path $PSScriptRoot '..\..\docs\performance\benchmark-cold-start.csv'),
    [switch]$AppendCsv
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BenchmarkCommon.psm1') -Force

try {
    if (-not $BaseUrl) { $BaseUrl = if ($Target -eq 'ollama') { [uri]'http://127.0.0.1:11434' } else { [uri]'http://localhost:5290' } }
    Assert-LoopbackBaseUrl $BaseUrl -RejectOllamaPort:($Target -eq 'backend')
    Assert-LoopbackBaseUrl $OllamaUrl
    $ollamaCli = Get-Command ollama -ErrorAction Stop
    $outputDirectory = [IO.Path]::GetDirectoryName($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputCsv))
    if (-not [IO.Directory]::Exists($outputDirectory)) { throw "Output directory must already exist: $outputDirectory" }
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}

$script = if ($Target -eq 'ollama') { 'ollama-benchmark.ps1' } else { 'backend-benchmark.ps1' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cold-start-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$cold = [Collections.Generic.List[object]]::new()
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(10)
try {
    for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
        & $ollamaCli.Source stop qwen3:4b *> $null
        $deadline = [DateTime]::UtcNow.AddSeconds($UnloadWaitSec)
        do {
            $loaded = @((($client.GetStringAsync([uri]::new($OllamaUrl, '/api/ps')).GetAwaiter().GetResult()) | ConvertFrom-Json).models)
            if (-not $loaded.Count) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($loaded.Count) { throw "The model is still loaded after ollama stop (repetition $repeat)." }

        $csv = Join-Path $scratch "run-$repeat.csv"
        & (Join-Path $PSScriptRoot $script) -BaseUrl $BaseUrl -Concurrency 1 -Profiles short -Rounds 1 -WarmupRequests 0 `
            -ResourceIntervalMs 0 -ColdStartNote "Model unloaded with ollama stop; /api/ps empty (repetition $repeat)." `
            -OutputCsv $csv -OutputJson (Join-Path $scratch "run-$repeat.json") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Benchmark script failed in repetition $repeat." }
        $row = @(Import-Csv -LiteralPath $csv | Where-Object phase -eq 'cold')[0]
        if (-not $row) { throw "No cold row was recorded in repetition $repeat." }
        $row | Add-Member -NotePropertyName repetition -NotePropertyValue $repeat
        $cold.Add($row)
        Write-Host ("cold {0} #{1}: {2} HTTP {3} {4} ms" -f $Target, $repeat, $row.outcome, $row.http_status, $row.elapsed_ms)
    }
    if ($AppendCsv -and (Test-Path -LiteralPath $OutputCsv)) {
        $cold | Export-Csv -LiteralPath $OutputCsv -Append -NoTypeInformation -Encoding utf8NoBOM -UseQuotes AsNeeded
    } else {
        $cold | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding utf8NoBOM -UseQuotes AsNeeded
    }
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
} finally {
    $client.Dispose()
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
$latency = [double[]]@($cold | Where-Object outcome -eq 'success' | ForEach-Object { [double]$_.elapsed_ms } | Sort-Object)
Write-Host ("SUMMARY: target={0} repeats={1} success={2} min={3} median={4} max={5} ms" -f $Target, $cold.Count, $latency.Count,
    ($latency | Select-Object -First 1), (Get-Percentile $latency 50), ($latency | Select-Object -Last 1))
exit $(if ($latency.Count -eq $cold.Count) { 0 } else { 1 })
