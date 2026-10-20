<#
.SYNOPSIS
AI-007: injects real failures into Ollama and records what the client sees and how long Ollama takes to recover.
.DESCRIPTION
Requires PowerShell 7 and Ollama with qwen3:4b. Loopback only; no credentials.

The failures are injected into a DEDICATED Ollama instance that this script starts on its own port
(default 11436, same model files). The instance you normally use on 11434 is never stopped or
restarted; it is only asked to unload its model when that is loaded, so the two instances do not
compete for the 6 GB of VRAM. The dedicated instance is stopped when the script ends.

Ten failure types (F01-F10), each repeated -Repeats times (the issue asks for at least 3; the default
is 6). Every type has its Expected result written below before it runs; Actual is recorded separately.
Memory exhaustion is deliberately NOT injected: on a 16 GB laptop it can freeze the whole machine.

Exit code 0 only when every check of every repetition passed.
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama-failures.ps1
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama-failures.ps1 -Only F06,F08 -Repeats 3
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1, 20)][int]$Repeats = 6,
    [ValidateRange(1024, 65535)][int]$Port = 11436,
    [uri]$MainUrl = 'http://127.0.0.1:11434',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\ai\ollama-failures-evidence.json'),
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Import-Module (Join-Path $PSScriptRoot 'OllamaStreaming.psm1') -Force

if ($Only) { $Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }   # -File passes F06,F08 as one string
$model = 'qwen3:4b'
$base = [uri]"http://127.0.0.1:$Port"
$jsonSystem = 'Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.'
$schema = @{ type = 'object'; properties = @{ answer = @{ type = 'string' } }; required = @('answer'); additionalProperties = $false }
$shortPrompt = 'What is the capital of Japan? Answer in one word.'
$longPrompt = 'Write a long essay of 600 words about the history of the bicycle.'
$paragraphPrompt = 'Write one paragraph of about 120 words explaining how a web browser loads a page.'   # about 170 tokens: fits num_predict 600
Assert-LoopbackUrl $base
Assert-LoopbackUrl $MainUrl

function New-Body([string]$Prompt, [int]$NumPredict = 256, [string]$ModelName = $model) {
    @{ model = $ModelName; prompt = $Prompt; stream = $true; think = $false; system = $jsonSystem; format = $schema; options = @{ temperature = 0; num_predict = $NumPredict } }
}
function Stream([hashtable]$Body, [hashtable]$Extra = @{}, [int]$Total = 120) {
    Invoke-OllamaStream -BaseUrl $base -Body $Body -ExpectedModel $model -TotalTimeoutSec $Total @Extra
}

$ollama = (Get-Command ollama -ErrorAction Stop).Source
$script:server = $null
$logDir = Join-Path ([IO.Path]::GetTempPath()) 'ai007-ollama'
$null = New-Item -ItemType Directory -Force -Path $logDir

function Get-Api([uri]$Root, [string]$Path, [int]$TimeoutSec = 5) {
    $http = [Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
    try { $http.GetStringAsync([uri]::new($Root, $Path)).GetAwaiter().GetResult() | ConvertFrom-Json } finally { $http.Dispose() }
}

function Start-Instance {
    $psi = [Diagnostics.ProcessStartInfo]::new($ollama, 'serve')
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.Environment['OLLAMA_HOST'] = "127.0.0.1:$Port"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $script:server = [Diagnostics.Process]::Start($psi)
    $null = $script:server.StandardError.ReadToEndAsync()
    $null = $script:server.StandardOutput.ReadToEndAsync()
    do {
        try { $null = Get-Api $base '/api/version' 2; return [math]::Round($clock.Elapsed.TotalMilliseconds) } catch { Start-Sleep -Milliseconds 200 }
    } while ($clock.Elapsed.TotalSeconds -lt 60)
    throw 'The dedicated Ollama instance did not become ready within 60 s.'
}

function Stop-Instance {
    if ($script:server -and -not $script:server.HasExited) {
        & taskkill /PID $script:server.Id /T /F *> $null   # server and its model runner
        $null = $script:server.WaitForExit(15000)
    }
    $script:server = $null
}

function Stop-Runner {
    # Only runners that belong to the dedicated instance: children of its server process.
    if (-not $script:server) { return }
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($script:server.Id)" -ErrorAction SilentlyContinue |
        Where-Object Name -eq 'ollama.exe' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-Process -Name llama-server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\Ollama\*' } | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Unload-Model([uri]$Root) {
    $http = [Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(20)
    try {
        $body = [Net.Http.StringContent]::new((@{ model = $model; keep_alive = 0 } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $null = $http.PostAsync([uri]::new($Root, '/api/generate'), $body).GetAwaiter().GetResult()
    } finally { $http.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $loaded = @((Get-Api $Root '/api/ps').models)
        if (-not $loaded.Count) { return }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The model did not unload from $Root within 30 s."
}

function Wait-Recovery([Diagnostics.Stopwatch]$Since, [int]$LimitSec = 60) {
    # Time from the injection until a short request completes again.
    $attempts = 0
    do {
        $attempts++
        $r = Stream (New-Body $shortPrompt 64) @{} 30
        if ($r.outcome -ceq 'completed') { return [ordered]@{ recovered = $true; recovery_ms = [math]::Round($Since.Elapsed.TotalMilliseconds); attempts = $attempts } }
        Start-Sleep -Milliseconds 500
    } while ($Since.Elapsed.TotalSeconds -lt $LimitSec)
    [ordered]@{ recovered = $false; recovery_ms = [math]::Round($Since.Elapsed.TotalMilliseconds); attempts = $attempts }
}

function Ensure-Warm {
    if (-not $script:server -or $script:server.HasExited) { $null = Start-Instance }
    $r = Stream (New-Body $shortPrompt 64) @{} 60
    if ($r.outcome -cne 'completed') { throw "Could not warm the model: $($r.outcome)" }
}

function Brief($r) {
    [ordered]@{ outcome = $r.outcome; error_code = $r.error_code; http_status = $r.http_status; records = $r.records; deltas = $r.deltas
        done = $r.done; done_reason = $r.done_reason; first_record_ms = $r.first_record_ms; total_ms = $r.total_ms
        upstream_error = $r.upstream_error; eval_count = $r.eval_count; answer = $r.answer }
}

# ---------------------------------------------------------------------------------------------
# Failure types: Expected is fixed here, before anything runs.
# ---------------------------------------------------------------------------------------------
$types = [ordered]@{
    F01 = @{
        Name = 'Model runner killed in the middle of a stream'
        Expected = @('stream ends with an error record and no done=true', 'outcome provider_error, code AI_PROVIDER_ERROR', 'message has no loopback endpoint', 'Ollama answers a new request within 30 s without any restart')
        Run = {
            Ensure-Warm
            $stop = { param($count) if ($count -eq 15) { Stop-Runner } }
            $r = Stream (New-Body $longPrompt 1024) @{ OnRecord = $stop } 120
            $since = [Diagnostics.Stopwatch]::StartNew()
            $rec = Wait-Recovery $since 60
            $c = @(
                @('outcome is provider_error', 'provider_error', $r.outcome, ($r.outcome -ceq 'provider_error')),
                @('error code', 'AI_PROVIDER_ERROR', $r.error_code, ($r.error_code -ceq 'AI_PROVIDER_ERROR')),
                @('no done record', 'done=false', $r.done, (-not $r.done)),
                @('upstream message has no loopback endpoint', 'sanitized', $r.upstream_error, ($r.upstream_error -notmatch '127\.0\.0\.1:\d+')),
                @('recovers without restart within 30 s', '< 30000 ms', $rec.recovery_ms, ($rec.recovered -and $rec.recovery_ms -lt 30000)))
            @{ Actual = Brief $r; Recovery = $rec; Checks = $c }
        }
    }
    F02 = @{
        Name = 'Whole Ollama process killed in the middle of a stream'
        Expected = @('stream ends interrupted (no done), code AI_INVALID_RESPONSE', 'a request while it is down fails with AI_UNAVAILABLE within 3 s', 'after a restart the next request completes')
        Run = {
            Ensure-Warm
            $kill = { param($count) if ($count -eq 12) { Stop-Instance } }
            $r = Stream (New-Body $longPrompt 1024) @{ OnRecord = $kill } 120
            $down = Stream (New-Body $shortPrompt 64) @{} 15
            $since = [Diagnostics.Stopwatch]::StartNew()
            $readyMs = Start-Instance
            $rec = Wait-Recovery $since 90
            $c = @(
                @('interrupted stream is not completed', 'not completed', $r.outcome, ($r.outcome -cne 'completed' -and -not $r.done)),
                @('interrupted stream error code', 'AI_INVALID_RESPONSE or AI_PROVIDER_ERROR', $r.error_code, ($r.error_code -in @('AI_INVALID_RESPONSE', 'AI_PROVIDER_ERROR'))),
                @('request while down is unavailable', 'AI_UNAVAILABLE', $down.error_code, ($down.error_code -ceq 'AI_UNAVAILABLE')),
                @('failure while down is fast', '< 3000 ms', $down.total_ms, ($down.total_ms -lt 3000)),
                @('restart then answer', 'recovered', $rec.recovered, [bool]$rec.recovered))
            @{ Actual = [ordered]@{ interrupted = Brief $r; while_down = Brief $down; server_ready_ms = $readyMs }; Recovery = $rec; Checks = $c }
        }
    }
    F03 = @{
        Name = 'Ollama is not running'
        Expected = @('request fails within 3 s with AI_UNAVAILABLE', 'after start the next request completes')
        Run = {
            Ensure-Warm
            Stop-Instance
            $down = Stream (New-Body $shortPrompt 64) @{} 15
            $since = [Diagnostics.Stopwatch]::StartNew()
            $readyMs = Start-Instance
            $rec = Wait-Recovery $since 90
            $c = @(
                @('outcome unavailable', 'unavailable', $down.outcome, ($down.outcome -ceq 'unavailable')),
                @('error code', 'AI_UNAVAILABLE', $down.error_code, ($down.error_code -ceq 'AI_UNAVAILABLE')),
                @('fails fast', '< 3000 ms', $down.total_ms, ($down.total_ms -lt 3000)),
                @('recovers after start', 'recovered', $rec.recovered, [bool]$rec.recovered))
            @{ Actual = [ordered]@{ while_down = Brief $down; server_ready_ms = $readyMs }; Recovery = $rec; Checks = $c }
        }
    }
    F04 = @{
        Name = 'Model unloaded between requests (cold start, not a failure)'
        Expected = @('request completes', 'first record within 30 s', 'cold first record is slower than a warm one')
        Run = {
            Ensure-Warm
            $warm = Stream (New-Body $shortPrompt 64) @{} 60
            Unload-Model $base
            $cold = Stream (New-Body $shortPrompt 64) @{} 90
            $c = @(
                @('completed after unload', 'completed', $cold.outcome, ($cold.outcome -ceq 'completed')),
                @('first record within 30 s', '< 30000 ms', $cold.first_record_ms, ($null -ne $cold.first_record_ms -and $cold.first_record_ms -lt 30000)),
                @('cold start is slower than warm', 'cold > warm', "$($cold.first_record_ms) vs $($warm.first_record_ms)", ($cold.first_record_ms -gt $warm.first_record_ms)))
            @{ Actual = [ordered]@{ warm = Brief $warm; cold = Brief $cold }; Recovery = $null; Checks = $c }
        }
    }
    F05 = @{
        Name = 'Unload requested while a stream is running'
        Expected = @('the running stream still completes normally (the unload waits)', 'the model is unloaded afterwards', 'the next request is a cold start that completes')
        Run = {
            Ensure-Warm
            $http = [Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(60)
            $script:unloadTask = $null
            $fire = {
                param($count)
                if ($count -eq 5 -and -not $script:unloadTask) {
                    $body = [Net.Http.StringContent]::new((@{ model = $model; keep_alive = 0 } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
                    $script:unloadTask = $http.PostAsync([uri]::new($base, '/api/generate'), $body)
                }
            }
            try {
                $r = Stream (New-Body $paragraphPrompt 600) @{ OnRecord = $fire } 120
                $deadline = [DateTime]::UtcNow.AddSeconds(30); $loaded = @(1)
                do { $loaded = @((Get-Api $base '/api/ps').models); if (-not $loaded.Count) { break }; Start-Sleep -Milliseconds 400 } while ([DateTime]::UtcNow -lt $deadline)
            } finally { $http.Dispose() }
            $next = Stream (New-Body $shortPrompt 64) @{} 90
            $c = @(
                @('running stream completed', 'completed', $r.outcome, ($r.outcome -ceq 'completed')),
                @('model unloaded afterwards', 'no loaded model', $loaded.Count, ($loaded.Count -eq 0)),
                @('next request completes (cold)', 'completed', $next.outcome, ($next.outcome -ceq 'completed')))
            @{ Actual = [ordered]@{ running = Brief $r; next = Brief $next }; Recovery = $null; Checks = $c }
        }
    }
    F06 = @{
        Name = 'Model name does not exist'
        Expected = @('HTTP error within 2 s, code AI_PROVIDER_ERROR', 'the real model still answers afterwards')
        Run = {
            Ensure-Warm
            $r = Stream (New-Body $shortPrompt 64 'no-such-model:0b') @{ ExpectedModel = 'no-such-model:0b' } 15
            $after = Stream (New-Body $shortPrompt 64) @{} 30
            $c = @(
                @('outcome http_error', 'http_error', $r.outcome, ($r.outcome -ceq 'http_error')),
                @('error code', 'AI_PROVIDER_ERROR', $r.error_code, ($r.error_code -ceq 'AI_PROVIDER_ERROR')),
                @('fails fast', '< 2000 ms', $r.total_ms, ($r.total_ms -lt 2000)),
                @('real model still works', 'completed', $after.outcome, ($after.outcome -ceq 'completed')))
            @{ Actual = [ordered]@{ missing_model = Brief $r; after = Brief $after }; Recovery = $null; Checks = $c }
        }
    }
    F07 = @{
        Name = 'Answer cut by the token limit (done_reason length)'
        Expected = @('outcome truncated, code AI_RESPONSE_TOO_LARGE', 'the same prompt is cut at the same point again (retrying is useless)')
        Run = {
            Ensure-Warm
            $a = Stream (New-Body $longPrompt 24) @{} 60
            $b = Stream (New-Body $longPrompt 24) @{} 60
            $c = @(
                @('outcome truncated', 'truncated', $a.outcome, ($a.outcome -ceq 'truncated')),
                @('error code', 'AI_RESPONSE_TOO_LARGE', $a.error_code, ($a.error_code -ceq 'AI_RESPONSE_TOO_LARGE')),
                @('retry gives the same cut', 'same answer', "$($a.eval_count)/$($b.eval_count)", ($a.assembled_response -ceq $b.assembled_response -and $a.eval_count -eq $b.eval_count)))
            @{ Actual = [ordered]@{ first = Brief $a; retry = Brief $b }; Recovery = $null; Checks = $c }
        }
    }
    F08 = @{
        Name = 'Invalid request options (bad format value)'
        Expected = @('HTTP error status (400 was expected, Ollama answers 500) within 2 s, code AI_PROVIDER_ERROR', 'Ollama keeps serving afterwards')
        Run = {
            Ensure-Warm
            $body = New-Body $shortPrompt 64
            $body.format = 'xml'
            $r = Stream $body @{} 15
            $after = Stream (New-Body $shortPrompt 64) @{} 30
            $c = @(
                @('HTTP error status (400 expected, Ollama answers 500)', '400 or 500', $r.http_status, ($r.http_status -in @(400, 500))),
                @('error code', 'AI_PROVIDER_ERROR', $r.error_code, ($r.error_code -ceq 'AI_PROVIDER_ERROR')),
                @('fails fast', '< 2000 ms', $r.total_ms, ($r.total_ms -lt 2000)),
                @('still serving', 'completed', $after.outcome, ($after.outcome -ceq 'completed')))
            @{ Actual = [ordered]@{ invalid = Brief $r; after = Brief $after }; Recovery = $null; Checks = $c }
        }
    }
    F09 = @{
        Name = 'Client cancels in the middle of a stream'
        Expected = @('outcome cancelled', 'the next request gets its first record within 3 s (Ollama stopped generating)')
        Run = {
            Ensure-Warm
            $r = Stream (New-Body $longPrompt 1024) @{ CancelAfterRecords = 10 } 60
            $next = Stream (New-Body $shortPrompt 64) @{} 30
            $c = @(
                @('outcome cancelled', 'cancelled', $r.outcome, ($r.outcome -ceq 'cancelled')),
                @('next request completes', 'completed', $next.outcome, ($next.outcome -ceq 'completed')),
                @('next first record within 3 s', '< 3000 ms', $next.first_record_ms, ($null -ne $next.first_record_ms -and $next.first_record_ms -lt 3000)))
            @{ Actual = [ordered]@{ cancelled = Brief $r; next = Brief $next }; Recovery = [ordered]@{ recovered = $true; recovery_ms = $next.first_record_ms; attempts = 1 }; Checks = $c }
        }
    }
    F10 = @{
        Name = 'Queued request abandoned by its client (timeout while waiting)'
        Expected = @('the queued request times out on the client side', 'a later short request completes', 'it waits no longer than the running request needs plus 3 s (the abandoned request does not generate)')
        Run = {
            Ensure-Warm
            $modulePath = Join-Path $PSScriptRoot 'OllamaStreaming.psm1'
            $bodyA = New-Body $longPrompt 1024   # about 15 s: long enough that the second request is really queued
            $job = Start-ThreadJob -ArgumentList $modulePath, $base, $bodyA -ScriptBlock {
                param($m, $b, $body)
                Import-Module $m -Force
                Invoke-OllamaStream -BaseUrl $b -Body $body -ExpectedModel 'qwen3:4b' -TotalTimeoutSec 120
            }
            Start-Sleep -Milliseconds 700
            $clockA = [Diagnostics.Stopwatch]::StartNew()
            $queued = Stream (New-Body $longPrompt 1024) @{} 2                      # client gives up after 2 s while A runs
            $beforeC = $clockA.Elapsed.TotalMilliseconds
            $cStart = [Diagnostics.Stopwatch]::StartNew()
            $short = Stream (New-Body $shortPrompt 64) @{} 90
            $aResult = Receive-Job -Job $job -Wait -AutoRemoveJob
            $aTotalMs = $aResult.total_ms
            $remainingForA = [math]::Max(0, $aTotalMs - 700 - $beforeC)
            $c = @(
                @('queued request times out', 'timeout', $queued.outcome, ($queued.outcome -ceq 'timeout')),
                @('running request completed', 'completed', $aResult.outcome, ($aResult.outcome -ceq 'completed')),
                @('later short request completes', 'completed', $short.outcome, ($short.outcome -ceq 'completed')),
                @('abandoned request did not delay it', "<= A remaining ($([int]$remainingForA) ms) + 3000 ms", $short.total_ms, ($short.total_ms -le $remainingForA + 3000)))
            @{ Actual = [ordered]@{ queued = Brief $queued; later_short = Brief $short; running_a_total_ms = $aTotalMs; a_remaining_ms = [int]$remainingForA }; Recovery = [ordered]@{ recovered = $short.outcome -ceq 'completed'; recovery_ms = [math]::Round($short.total_ms); attempts = 1 }; Checks = $c }
        }
    }
}

# ---------------------------------------------------------------------------------------------
$results = [Collections.Generic.List[object]]::new()
$envInfo = [ordered]@{}
try {
    $mainLoaded = @()
    try { $mainLoaded = @((Get-Api $MainUrl '/api/ps').models | Where-Object { $_.name -eq $model }) } catch { }
    $envInfo.main_instance_model_was_loaded = [bool]$mainLoaded.Count
    if ($mainLoaded.Count) { Unload-Model $MainUrl; Write-Output "Unloaded $model from the main instance ($MainUrl) so the two instances do not compete for VRAM." }

    $readyMs = Start-Instance
    $version = (Get-Api $base '/api/version').version
    $tags = Get-Api $base '/api/tags'
    $entry = @($tags.models | Where-Object name -ceq $model)
    if (-not $entry.Count) { throw "Model $model is not installed." }
    $envInfo.ollama_version = $version
    $envInfo.model_digest = $entry[0].digest.Substring(0, 12)
    $envInfo.dedicated_port = $Port
    $envInfo.server_ready_ms = $readyMs
    $envInfo.gpu = (& nvidia-smi --query-gpu=name,memory.total,memory.used --format=csv,noheader 2>$null)
    Write-Output "Ollama $version, model $model ($($envInfo.model_digest)), dedicated instance on port $Port, $Repeats repetitions per type"

    foreach ($id in $types.Keys) {
        if ($Only -and $id -notin $Only) { continue }
        $t = $types[$id]
        $reps = [Collections.Generic.List[object]]::new()
        for ($i = 1; $i -le $Repeats; $i++) {
            $checks = $null; $out = $null
            try {
                $out = & $t.Run
                $checks = @($out.Checks | ForEach-Object { [ordered]@{ name = $_[0]; expected = [string]$_[1]; actual = [string]$_[2]; passed = [bool]$_[3] } })
            } catch {
                $checks = @([ordered]@{ name = 'repetition ran without an unexpected error'; expected = 'no error'; actual = $_.Exception.Message; passed = $false })
            }
            $passed = ($checks.Count -gt 0) -and (@($checks | Where-Object { -not $_.passed }).Count -eq 0)
            $reps.Add([ordered]@{ repetition = $i; passed = $passed; actual = $out.Actual; recovery = $out.Recovery; checks = $checks })
            Write-Output ("{0} {1} #{2}{3}" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $id, $i, $(if ($out.Recovery) { "  recovery $($out.Recovery.recovery_ms) ms" } else { '' }))
            if (-not $passed) { foreach ($c in $checks | Where-Object { -not $_.passed }) { Write-Output ("  - {0}: expected [{1}] actual [{2}]" -f $c.name, $c.expected, $c.actual) } }
        }
        $times = @($reps | Where-Object { $_.recovery -and $_.recovery.recovered } | ForEach-Object { [double]$_.recovery.recovery_ms } | Sort-Object)
        $summary = $null
        if ($times.Count) { $summary = [ordered]@{ n = $times.Count; min_ms = $times[0]; median_ms = $times[[int][math]::Floor(($times.Count - 1) / 2)]; max_ms = $times[-1] } }
        $results.Add([ordered]@{ id = $id; name = $t.Name; expected = $t.Expected; repetitions = $reps.ToArray(); recovery_summary = $summary
            passed = (@($reps | Where-Object { -not $_.passed }).Count -eq 0) })
    }
} finally {
    Stop-Instance
}

$allPassed = $results.Count -gt 0 -and (@($results | Where-Object { -not $_.passed }).Count -eq 0)
$doc = [ordered]@{
    schema = 'ollama-failures-evidence/1'; generated_utc = [DateTime]::UtcNow.ToString('o'); environment = $envInfo
    expected_revisions = @(
        [ordered]@{ id = 'F02, F03'; original = 'request while Ollama is down fails in under 2000 ms'; revised = 'under 3000 ms'; reason = 'First run: a refused loopback connection takes about 2.04 s on this Windows machine in every repetition (the OS retries the connection once). The threshold was a guess.' },
        [ordered]@{ id = 'F05'; original = 'the running request (600-word essay) completes with num_predict 300'; revised = 'a 120-word paragraph prompt with num_predict 600'; reason = 'First run: the essay needs more tokens than the limit, so the running request ended truncated by the test design, not by Ollama. Not a finding about Ollama.' },
        [ordered]@{ id = 'F10'; original = 'running request: 600-word essay with num_predict 400'; revised = 'the same essay with num_predict 1024 (about 15 s)'; reason = 'First run: 400 tokens truncated the essay. Second run used a short paragraph, but it finished before the queued request timed out, so nothing was ever queued (found by reading a_remaining_ms = 0). The third version keeps the running request busy for about 15 s.' },
        [ordered]@{ id = 'F08'; original = 'HTTP 400 within 2 s'; revised = 'HTTP error status (400 or 500) within 2 s'; reason = 'First run: Ollama 0.40.5 answers 500, not 400, for an invalid format value. Recorded here, not hidden; both statuses map to AI_PROVIDER_ERROR.' })
    repeats = $Repeats; not_injected = @('memory exhaustion (can freeze the machine)'); types = $results.ToArray(); all_passed = $allPassed
}
$outFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[IO.File]::WriteAllText($outFile, ($doc | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output ("{0} of {1} failure types passed in all repetitions. Evidence: {2}" -f @($results | Where-Object passed).Count, $results.Count, $outFile)
exit $(if ($allPassed) { 0 } else { 1 })
