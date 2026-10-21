<#
.SYNOPSIS
AI-006: (1) requests arriving at random times instead of in a burst, (2) cancellation of requests that are still waiting in the queue.
.DESCRIPTION
Requires PowerShell 7. Loopback only. Uses the same prompts, request bodies and success rules as the AI-004 benchmark.

-Mode Arrival   Poisson arrivals at -Rate requests per second for -DurationSec seconds, for one profile.
                Reports latency percentiles of the requests that arrived (queue wait included), success rate and the
                number still unfinished when the arrivals stopped. Rates above the capacity make the queue grow,
                which is the point of the test.
-Mode Cancel    -Burst requests are sent at once; the -CancelCount requests right behind the running one are abandoned by the
                client after -CancelAfterMs while still queued (the connection is closed). The time until the REMAINING requests all finish
                is compared with a control burst in which nobody cancels. If abandoned requests release the queue the
                cancel runs finish earlier; if Ollama still generates for them they do not.

Exit 0 when the run finished and results were written. Nothing is changed on Ollama or the Backend.
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\arrival-and-cancel-benchmark.ps1 -Mode Arrival -Target ollama -Profile medium -Rate 0.5 -DurationSec 120 -OutputJson out.json
.EXAMPLE
pwsh -NoProfile -File .\scripts\benchmark\arrival-and-cancel-benchmark.ps1 -Mode Cancel -Target backend -Profile medium -Repeats 5 -OutputJson cancel.json
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Arrival', 'Cancel')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('ollama', 'backend')][string]$Target,
    [ValidateSet('short', 'medium', 'long')][string]$Profile = 'medium',
    [uri]$BaseUrl,
    [double]$Rate = 0.5,
    [ValidateRange(5, 1800)][int]$DurationSec = 120,
    [ValidateRange(2, 20)][int]$Burst = 6,
    [ValidateRange(1, 19)][int]$CancelCount = 3,
    [ValidateRange(100, 20000)][int]$CancelAfterMs = 500,
    [ValidateRange(1, 50)][int]$Repeats = 5,
    [ValidateRange(1, 1800)][int]$TimeoutSec = 600,
    [int]$Seed = 20261020,
    [string]$SettingsPath = (Join-Path $PSScriptRoot '..\..\BC-LocalAI\appsettings.json'),
    [Parameter(Mandatory)][string]$OutputJson
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BenchmarkCommon.psm1') -Force
if (-not $BaseUrl) { $BaseUrl = if ($Target -eq 'ollama') { [uri]'http://127.0.0.1:11434' } else { [uri]'http://localhost:5290' } }
Assert-LoopbackBaseUrl $BaseUrl -RejectOllamaPort:($Target -eq 'backend')

$model = 'qwen3:4b'
$systemPrompt = 'Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.'
$numPredict = 256
if (Test-Path -LiteralPath $SettingsPath) { $cfg = (Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json).Ollama.MaxOutputTokens; if ($null -ne $cfg) { $numPredict = [int]$cfg } }
$prompts = @((Get-BenchmarkPrompts)[$Profile])
$endpoint = if ($Target -eq 'ollama') { [uri]::new($BaseUrl, '/api/generate') } else { [uri]::new($BaseUrl, '/api/chat') }

function New-RequestBody([string]$Prompt) {
    if ($Target -eq 'backend') { return (@{ message = $Prompt } | ConvertTo-Json -Compress) }
    @{ model = $model; prompt = $Prompt; stream = $false; think = $false; system = $systemPrompt
       format = @{ type = 'object'; properties = @{ answer = @{ type = 'string' } }; required = @('answer'); additionalProperties = $false }
       options = @{ temperature = 0; num_predict = $numPredict } } | ConvertTo-Json -Depth 8 -Compress
}

$handler = [Net.Http.SocketsHttpHandler]::new(); $handler.UseProxy = $false; $handler.MaxResponseDrainSize = 0
$client = [Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
$rng = [Random]::new($Seed)

function Send([string]$Body, [Threading.CancellationToken]$Token = [Threading.CancellationToken]::None) {
    $content = [Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json')
    [pscustomobject]@{ Task = $client.PostAsync($endpoint, $content, $Token); Content = $content; StartMs = $null; EndMs = $null; Done = $false }
}

function Complete([object]$Item, [Diagnostics.Stopwatch]$Clock) {
    # Stamp the end time as soon as the loop sees the task finished (resolution = loop interval).
    if (-not $Item.Done -and $Item.Task.IsCompleted) { $Item.EndMs = $Clock.Elapsed.TotalMilliseconds; $Item.Done = $true }
}

function Read-Outcome([object]$Item) {
    try {
        $response = $Item.Task.GetAwaiter().GetResult()
        try { $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); $status = [int]$response.StatusCode } finally { $response.Dispose() }
        (ConvertTo-BenchmarkOutcome -Target $Target -Status $status -Raw $raw -Model $model).outcome
    } catch [Threading.Tasks.TaskCanceledException] { if ($Item.Task.IsCanceled) { 'abandoned_by_client' } else { 'client_timeout' } }
    catch { 'connection_error' }
    finally { $Item.Content.Dispose() }
}

function Percentile([double[]]$Values, [double]$P) {
    if (-not $Values.Count) { return $null }
    $sorted = @($Values | Sort-Object); Get-Percentile $sorted $P
}

$result = [ordered]@{ mode = $Mode; target = $Target; profile = $Profile; endpoint = $endpoint.AbsoluteUri; num_predict = $numPredict; tested_utc = [DateTime]::UtcNow.ToString('o') }

if ($Mode -eq 'Arrival') {
    # Warm the model so the first arrival is not a cold start.
    $warm = Send (New-RequestBody $prompts[0]); $null = $warm.Task.GetAwaiter().GetResult().Dispose()
    $arrivals = [Collections.Generic.List[double]]::new(); $t = 0.0
    while ($true) { $t += -[math]::Log(1 - $rng.NextDouble()) / $Rate; if ($t -ge $DurationSec) { break }; $arrivals.Add($t * 1000) }
    $clock = [Diagnostics.Stopwatch]::StartNew(); $items = [Collections.Generic.List[object]]::new(); $next = 0
    while ($next -lt $arrivals.Count -or @($items | Where-Object { -not $_.Done }).Count) {
        $now = $clock.Elapsed.TotalMilliseconds
        while ($next -lt $arrivals.Count -and $arrivals[$next] -le $now) {
            $it = Send (New-RequestBody $prompts[$next % $prompts.Count]); $it.StartMs = $clock.Elapsed.TotalMilliseconds; $items.Add($it); $next++
        }
        foreach ($it in $items) { Complete $it $clock }
        if ($clock.Elapsed.TotalSeconds -gt ($DurationSec + $TimeoutSec)) { break }
        Start-Sleep -Milliseconds 10
    }
    $stoppedAtMs = [double]($arrivals[-1])
    $rows = foreach ($it in $items) {
        $outcome = if ($it.Done) { Read-Outcome $it } else { 'unfinished' }
        [pscustomobject]@{ start_ms = [math]::Round($it.StartMs, 1); latency_ms = if ($it.Done) { [math]::Round($it.EndMs - $it.StartMs, 1) } else { $null }; outcome = $outcome; finished_after_arrivals_stopped = ($it.Done -and $it.EndMs -gt $stoppedAtMs) }
    }
    $ok = @($rows | Where-Object outcome -eq 'success')
    $lat = [double[]]@($ok | ForEach-Object latency_ms)
    $result.rate_rps = $Rate; $result.duration_sec = $DurationSec; $result.arrived = $items.Count
    $result.success = $ok.Count; $result.unfinished = @($rows | Where-Object outcome -eq 'unfinished').Count
    $result.other_failures = @($rows | Where-Object { $_.outcome -notin @('success', 'unfinished') } | Group-Object outcome | ForEach-Object { "$($_.Name)=$($_.Count)" })
    $result.latency_p50_ms = Percentile $lat 50; $result.latency_p95_ms = Percentile $lat 95; $result.latency_p99_ms = Percentile $lat 99; $result.latency_max_ms = if ($lat.Count) { ($lat | Measure-Object -Maximum).Maximum } else { $null }
    $firstQuarter = [double[]]@($ok | Where-Object { $_.start_ms -lt $DurationSec * 250 } | ForEach-Object latency_ms)
    $lastQuarter = [double[]]@($ok | Where-Object { $_.start_ms -ge $DurationSec * 750 } | ForEach-Object latency_ms)
    $result.latency_p50_first_quarter_ms = Percentile $firstQuarter 50; $result.latency_p50_last_quarter_ms = Percentile $lastQuarter 50
    $result.requests = @($rows)
}
else {
    $warm = Send (New-RequestBody $prompts[0]); $null = $warm.Task.GetAwaiter().GetResult().Dispose()
    $runs = [Collections.Generic.List[object]]::new()
    foreach ($kind in @('control', 'cancel')) {
        for ($rep = 1; $rep -le $Repeats; $rep++) {
            $clock = [Diagnostics.Stopwatch]::StartNew(); $items = [Collections.Generic.List[object]]::new(); $ctsList = @()
            for ($i = 0; $i -lt $Burst; $i++) {
                $cts = [Threading.CancellationTokenSource]::new(); $ctsList += $cts
                $it = Send (New-RequestBody $prompts[($rep * $Burst + $i) % $prompts.Count]) $cts.Token
                $it.StartMs = $clock.Elapsed.TotalMilliseconds; $items.Add($it)
                if ($kind -eq 'cancel' -and $i -ge 1 -and $i -le $CancelCount) { $cts.CancelAfter($CancelAfterMs) }   # the ones right behind the running request are abandoned while queued
                Start-Sleep -Milliseconds 20   # fixed order: request 0 is served first
            }
            # Same requests are measured in both modes: request 0 and every request behind the ones that may be abandoned.
            $keepIdx = @(0) + @(($CancelCount + 1)..($Burst - 1))
            $keep = @($keepIdx | ForEach-Object { $items[$_] })
            while (@($keep | Where-Object { -not $_.Done }).Count -and $clock.Elapsed.TotalSeconds -lt $TimeoutSec) { foreach ($it in $items) { Complete $it $clock }; Start-Sleep -Milliseconds 10 }
            $outcomes = @($keep | ForEach-Object { if ($_.Done) { Read-Outcome $_ } else { 'unfinished' } })
            foreach ($it in ($items | Where-Object { $_ -notin $keep })) { $null = if ($it.Done) { Read-Outcome $it } else { $it.Content.Dispose() } }
            $finishAll = ($keep | Where-Object Done | ForEach-Object EndMs | Measure-Object -Maximum).Maximum
            $runs.Add([ordered]@{ kind = $kind; repetition = $rep; kept_requests = $keep.Count; kept_success = @($outcomes | Where-Object { $_ -eq 'success' }).Count
                time_until_kept_requests_finished_ms = [math]::Round($finishAll, 1); outcomes = $outcomes })
            foreach ($c in $ctsList) { $c.Dispose() }
            Start-Sleep -Seconds 2   # let any leftover generation end before the next burst
        }
    }
    $result.burst = $Burst; $result.cancel_count = $CancelCount; $result.cancel_after_ms = $CancelAfterMs; $result.repeats = $Repeats
    foreach ($k in 'control', 'cancel') {
        $v = [double[]]@($runs | Where-Object { $_.kind -eq $k } | ForEach-Object { $_.time_until_kept_requests_finished_ms })
        $result["${k}_median_ms"] = Percentile $v 50; $result["${k}_min_ms"] = ($v | Measure-Object -Minimum).Minimum; $result["${k}_max_ms"] = ($v | Measure-Object -Maximum).Maximum
    }
    $result.runs = $runs.ToArray()
}
$client.Dispose()
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputJson)
[IO.File]::WriteAllText($out, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $out"
exit 0
