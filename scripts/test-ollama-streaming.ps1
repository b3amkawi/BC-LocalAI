<#
.SYNOPSIS
AI-005: tests Ollama /api/generate streaming (NDJSON) with qwen3:4b and saves evidence.
.DESCRIPTION
Requires PowerShell 7 and a running Ollama with qwen3:4b. Loopback only; no credentials.
Each scenario records Expected and Actual values and a pass/fail per check. Exit 0 only when every
check of every scenario that ran passed; any failure returns exit 1.

Scenarios: English/Thai streaming, chunk assembly, Unicode and special characters, thinking
behaviour (think=false, think=true, no format), truncation, HTTP errors, empty prompt, client
cancellation, total timeout, unavailable endpoint and latency profile.

-IncludeRunnerStop is a failure injection: it stops the Ollama model runner process
(llama-server.exe under the Ollama install folder) in the middle of a stream to observe an
interrupted stream. Ollama starts a new runner on the next request. -IncludeColdStart runs
`ollama stop qwen3:4b` first to measure the first record after unloading the model.
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama-streaming.ps1
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama-streaming.ps1 -IncludeRunnerStop -IncludeColdStart
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://127.0.0.1:11434',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\ai\ollama-streaming-evidence.json'),
    [ValidateRange(5, 1800)][int]$TotalTimeoutSec = 180,
    [switch]$IncludeRunnerStop,
    [switch]$IncludeColdStart
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Import-Module (Join-Path $PSScriptRoot 'OllamaStreaming.psm1') -Force

$model = 'qwen3:4b'
$jsonSystem = 'Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.'
$plainSystem = 'Answer the user briefly in the same language as the user.'
$schema = @{ type = 'object'; properties = @{ answer = @{ type = 'string' } }; required = @('answer'); additionalProperties = $false }
$longPrompt = 'Write a long essay of 600 words about the history of the bicycle.'

function New-Body([string]$Prompt, [bool]$Think = $false, [bool]$Format = $true, [int]$NumPredict = 256, [string]$ModelName = $model) {
    $body = @{ model = $ModelName; prompt = $Prompt; stream = $true; think = $Think; options = @{ temperature = 0; num_predict = $NumPredict } }
    if ($Format) { $body.system = $jsonSystem; $body.format = $schema } else { $body.system = $plainSystem }
    return $body
}

try {
    Assert-LoopbackUrl $BaseUrl
    $outputDirectory = [IO.Path]::GetDirectoryName($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath))
    if (-not [IO.Directory]::Exists($outputDirectory)) { throw "Output directory must already exist: $outputDirectory" }
    $probe = [Net.Http.HttpClient]::new(); $probe.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        $version = ($probe.GetStringAsync([uri]::new($BaseUrl, '/api/version')).GetAwaiter().GetResult() | ConvertFrom-Json).version
        $tags = $probe.GetStringAsync([uri]::new($BaseUrl, '/api/tags')).GetAwaiter().GetResult() | ConvertFrom-Json
    } finally { $probe.Dispose() }
    if ($version -isnot [string] -or [string]::IsNullOrWhiteSpace($version)) { throw 'GET /api/version did not return an Ollama version.' }
    $installed = @($tags.models | Where-Object name -ceq $model)
    if (-not $installed.Count) { throw "Model $model is not installed (ollama pull $model)." }
} catch {
    [Console]::Error.WriteLine("FAIL: $($_.Exception.Message)")
    exit 1
}

$scenarios = [Collections.Generic.List[object]]::new()
$script:checks = $null

function Check([string]$Name, $Expected, $Actual, [bool]$Passed) {
    $script:checks.Add([ordered]@{ name = $Name; expected = [string]$Expected; actual = [string]$Actual; passed = $Passed })
}

function Brief($result) {
    [ordered]@{
        outcome = $result.outcome; error_code = $result.error_code; http_status = $result.http_status
        content_type = $result.content_type; records = $result.records; deltas = $result.deltas
        answer = $result.answer; done = $result.done; done_reason = $result.done_reason; has_thinking_field = $result.has_thinking_field
        thinking_chars = $result.thinking_chars; first_record_ms = $result.first_record_ms
        first_answer_ms = $result.first_answer_ms; completed_ms = $result.completed_ms; total_ms = $result.total_ms
        interval_mean_ms = $result.interval_mean_ms; interval_max_ms = $result.interval_max_ms
        eval_count = $result.eval_count; timeout_kind = $result.timeout_kind
        upstream_error = $result.upstream_error; problems = $result.problems
    }
}

function Run-Scenario([string]$Id, [string]$Name, [string[]]$Expected, [scriptblock]$Body) {
    $script:checks = [Collections.Generic.List[object]]::new()
    $actual = $null; $request = $null
    try {
        $output = & $Body
        $actual = $output.Actual; $request = $output.Request
    } catch {
        Check 'scenario ran without an unexpected error' 'no error' $_.Exception.Message $false
    }
    $passed = (@($script:checks | Where-Object { -not $_.passed }).Count -eq 0) -and $script:checks.Count -gt 0
    $scenarios.Add([ordered]@{ id = $Id; name = $Name; expected = $Expected; request = $request; actual = $actual; checks = $script:checks.ToArray(); passed = $passed })
    Write-Output ("{0} {1}: {2}" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $Id, $Name)
    if (-not $passed) { foreach ($c in $script:checks | Where-Object { -not $_.passed }) { Write-Output ("  - {0}: expected [{1}] actual [{2}]" -f $c.name, $c.expected, $c.actual) } }
}

function Stream([hashtable]$Body, [hashtable]$Extra = @{}) {
    Invoke-OllamaStream -BaseUrl $BaseUrl -Body $Body -ExpectedModel $model -TotalTimeoutSec $TotalTimeoutSec @Extra
}

function Assert-Completed($r, [string]$Label = '') {
    Check "${Label}outcome is completed" 'completed' $r.outcome ($r.outcome -ceq 'completed')
    Check "${Label}HTTP 200 and application/x-ndjson" '200 application/x-ndjson' "$($r.http_status) $($r.content_type)" ($r.http_status -eq 200 -and $r.content_type -ceq 'application/x-ndjson')
    Check "${Label}final record done=true with done_reason=stop" 'stop' $r.done_reason ($r.done_reason -ceq 'stop')
    Check "${Label}no thinking field in a think=false/format request" 'no thinking field' $r.has_thinking_field (-not $r.has_thinking_field)
    Check "${Label}no thinking marker in the answer" 'none' $r.answer ($r.answer -notmatch '(?i)</?think>')
    $parsed = try { ($r.assembled_response | ConvertFrom-Json -AsHashtable).answer } catch { $null }
    Check "${Label}decoded answer equals the answer of the assembled JSON" 'equal' $(if ($parsed -ceq $r.answer) { 'equal' } else { 'different' }) ($parsed -ceq $r.answer -and -not [string]::IsNullOrWhiteSpace($r.answer))
}

Write-Output "Ollama $version, model $model ($($installed[0].digest.Substring(0, 12)))"

if ($IncludeColdStart) {
    Run-Scenario 'S00' 'Cold start: first record after ollama stop' @('model unloaded before the request', 'request completes', 'first record arrives within 30 s') {
        $ollama = Get-Command ollama -ErrorAction Stop
        & $ollama.Source stop $model *> $null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $http = [Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(10)
        try { do { $loaded = @((($http.GetStringAsync([uri]::new($BaseUrl, '/api/ps')).GetAwaiter().GetResult()) | ConvertFrom-Json).models); if (-not $loaded.Count) { break }; Start-Sleep -Milliseconds 500 } while ([DateTime]::UtcNow -lt $deadline) } finally { $http.Dispose() }
        Check 'model is unloaded' 'no loaded model' $loaded.Count ($loaded.Count -eq 0)
        $body = New-Body 'What is the capital of Japan? Answer briefly.'
        $r = Stream $body
        Check 'outcome is completed' 'completed' $r.outcome ($r.outcome -ceq 'completed')
        Check 'first record within 30 s' '< 30000 ms' $r.first_record_ms ($null -ne $r.first_record_ms -and $r.first_record_ms -lt 30000)
        @{ Actual = Brief $r; Request = $body }
    }
}

Run-Scenario 'S01' 'English streaming (format + think=false)' @('HTTP 200, application/x-ndjson', 'several records, last has done=true and done_reason=stop', 'answer assembled from chunks equals the JSON answer', 'no thinking field') {
    $body = New-Body 'What is the capital of Japan? Answer briefly in English.'
    $r = Stream $body
    Assert-Completed $r
    Check 'several records' '>= 5' $r.records ($r.records -ge 5)
    Check 'answer mentions Tokyo' 'Tokyo' $r.answer ($r.answer -match 'Tokyo')
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S02' 'Thai streaming (format + think=false)' @('answer contains Thai characters', 'Thai syllables split across records are reassembled without replacement characters') {
    $body = New-Body 'ประเทศไทยมีเมืองหลวงชื่ออะไร ตอบสั้น ๆ เป็นภาษาไทย'
    $r = Stream $body
    Assert-Completed $r
    Check 'answer contains Thai characters' 'Thai characters' $r.answer ($r.answer -match '[ก-๎]')
    Check 'no replacement character (U+FFFD)' 'none' $r.answer ($r.answer -notmatch [string][char]0xFFFD)
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S03' 'Chunk assembly: the answer arrives incrementally' @('more than one answer delta', 'first answer delta arrives before the stream completes', 'concatenated deltas equal the answer') {
    $body = New-Body 'Describe the Sun in one sentence of about 25 words.' $false $true 160
    $r = Stream $body @{ KeepRecords = $true }
    Assert-Completed $r
    Check 'more than one delta' '>= 5' $r.deltas ($r.deltas -ge 5)
    Check 'first answer delta before completion' 'first_answer_ms < completed_ms' "$($r.first_answer_ms) / $($r.completed_ms)" ($null -ne $r.first_answer_ms -and $r.first_answer_ms -lt $r.completed_ms)
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S04' 'Unicode and special characters' @('emoji, quotes and a line break survive streaming and JSON escaping') {
    $cases = @(
        @{ prompt = 'Set answer to exactly: Hello 😊🎉 ครับ'; label = 'emoji'; test = { param($a) $a.Contains('😊') -and $a.Contains('🎉') -and $a.Contains('ครับ') } }
        @{ prompt = 'Set answer to exactly this text: He said "hi" twice'; label = 'quotes'; test = { param($a) $a.Contains('"hi"') } }
        @{ prompt = 'Set answer to a two-line text: first line A, second line B (use a real line break).'; label = 'line break'; test = { param($a) $a.Contains("`n") } }
    )
    $summary = [ordered]@{}
    foreach ($case in $cases) {
        $body = New-Body $case.prompt $false $true 128
        $body.system = 'Return a JSON object containing only an answer field. Put exactly the requested text in answer.'
        $r = Stream $body
        Check "$($case.label): outcome is completed" 'completed' $r.outcome ($r.outcome -ceq 'completed')
        Check "$($case.label): expected characters present" 'present' $r.answer ((& $case.test $r.answer) -eq $true)
        Check "$($case.label): no replacement character" 'none' $r.answer ($r.answer -notmatch [string][char]0xFFFD)
        $summary[$case.label] = Brief $r
    }
    @{ Actual = $summary; Request = 'three requests (emoji, quotes, line break)' }
}

Run-Scenario 'S05' 'Thinking: think=true is separated into a thinking field' @('thinking field present and never part of the answer', 'answer completes after the thinking', 'first answer delta arrives later than the first record') {
    $body = New-Body 'What is the capital of France?' $true $true 512
    $r = Stream $body
    Check 'outcome is completed' 'completed' $r.outcome ($r.outcome -ceq 'completed')
    Check 'thinking field present' 'true' $r.has_thinking_field $r.has_thinking_field
    Check 'thinking characters counted' '> 0' $r.thinking_chars ($r.thinking_chars -gt 0)
    Check 'answer does not contain the thinking text' 'no "the user"' $r.answer ($r.answer -notmatch '(?i)the user|let me|okay,')
    Check 'answer arrives after thinking' 'first_answer_ms > first_record_ms + 200' "$($r.first_answer_ms) vs $($r.first_record_ms)" ($null -ne $r.first_answer_ms -and $r.first_answer_ms -gt $r.first_record_ms + 200)
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S06' 'Observation: think=false without format leaks reasoning into response' @('stream is rejected (not completed) because the response is not the {answer} JSON', 'classified as invalid response') {
    $body = New-Body 'What is the capital of Japan? Answer in one sentence.' $false $false 256
    $r = Stream $body @{ KeepRecords = $true }
    Check 'outcome is not completed' 'invalid' $r.outcome ($r.outcome -ceq 'invalid')
    Check 'public error code' 'AI_INVALID_RESPONSE' $r.error_code ($r.error_code -ceq 'AI_INVALID_RESPONSE')
    $first = if ($r.record_samples.Count) { [string]$r.record_samples[0].response } else { '' }
    Check 'first fragment is prose, not a JSON object' 'does not start with {' $first (-not $first.TrimStart().StartsWith('{'))
    $actual = Brief $r; $actual.first_fragment = $first
    @{ Actual = $actual; Request = $body }
}

Run-Scenario 'S07' 'Truncation: done_reason=length is not a completion' @('outcome truncated', 'error code AI_RESPONSE_TOO_LARGE', 'the JSON answer is incomplete') {
    $body = New-Body 'Explain photosynthesis in detail.' $false $true 8
    $r = Stream $body
    Check 'outcome is truncated' 'truncated' $r.outcome ($r.outcome -ceq 'truncated')
    Check 'done_reason is length' 'length' $r.done_reason ($r.done_reason -ceq 'length')
    Check 'error code' 'AI_RESPONSE_TOO_LARGE' $r.error_code ($r.error_code -ceq 'AI_RESPONSE_TOO_LARGE')
    $parseable = $true
    try { $null = $r.assembled_response | ConvertFrom-Json } catch { $parseable = $false }
    Check 'the assembled response is not valid JSON' 'not parseable' $r.assembled_response (-not $parseable)
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S08' 'HTTP errors before the stream starts' @('unknown model: HTTP 404 with an error body', 'invalid option type: HTTP 500', 'malformed request JSON: HTTP 400', 'all classified as http_error / AI_PROVIDER_ERROR') {
    $summary = [ordered]@{}
    $notFound = Stream (New-Body 'hi' $false $true 16 'does-not-exist:1b')
    Check 'unknown model: HTTP 404' '404' $notFound.http_status ($notFound.http_status -eq 404)
    Check 'unknown model: http_error with a message' 'http_error' "$($notFound.outcome) / $($notFound.upstream_error)" ($notFound.outcome -ceq 'http_error' -and $notFound.upstream_error -match 'not found')
    $summary.unknown_model = Brief $notFound
    $badOption = New-Body 'hi' $false $true 16; $badOption.options.num_predict = 'abc'
    $option = Stream $badOption
    Check 'invalid option: HTTP 500 with an error' '500 http_error' "$($option.http_status) $($option.outcome)" ($option.http_status -eq 500 -and $option.outcome -ceq 'http_error')
    $summary.invalid_option = Brief $option
    $malformed = Stream @{} @{ RawBody = [Text.Encoding]::UTF8.GetBytes('{"model": "qwen3:4b", "prompt": ') }
    Check 'malformed JSON: HTTP 400' '400 http_error' "$($malformed.http_status) $($malformed.outcome)" ($malformed.http_status -eq 400 -and $malformed.outcome -ceq 'http_error')
    $summary.malformed_json = Brief $malformed
    Check 'error code is AI_PROVIDER_ERROR' 'AI_PROVIDER_ERROR' $notFound.error_code ($notFound.error_code -ceq 'AI_PROVIDER_ERROR')
    @{ Actual = $summary; Request = 'unknown model, invalid option, malformed JSON' }
}

Run-Scenario 'S09' 'Empty prompt returns done_reason=load, which is not a completion' @('HTTP 200 with a final record that has done_reason load', 'classified as invalid response') {
    $body = New-Body '' $false $true 16
    $r = Stream $body
    Check 'HTTP 200' '200' $r.http_status ($r.http_status -eq 200)
    Check 'done_reason is load' 'load' $r.done_reason ($r.done_reason -ceq 'load')
    Check 'outcome is invalid' 'invalid' $r.outcome ($r.outcome -ceq 'invalid')
    @{ Actual = Brief $r; Request = $body }
}

Run-Scenario 'S10' 'Client cancellation stops generation on the Ollama side' @('cancelled after 25 records', 'with the connection closed at once the next request starts immediately', 'with the .NET default drain (2 s) Ollama keeps generating and the next request waits about 2 s') {
    $short = New-Body 'Say hi.' $false $true 64
    $baseline = Stream $short
    Check 'baseline request completes' 'completed' $baseline.outcome ($baseline.outcome -ceq 'completed')
    $body = New-Body $longPrompt $true $true 1024
    $r = Stream $body @{ CancelAfterRecords = 25 }
    Check 'outcome is cancelled' 'cancelled' $r.outcome ($r.outcome -ceq 'cancelled')
    Check 'cancelled after 25 records' '25' $r.records ($r.records -eq 25)
    $follow = Stream $short
    Check 'next request completes' 'completed' $follow.outcome ($follow.outcome -ceq 'completed')
    Check 'next request starts at once (connection closed on cancel)' 'first record < 1000 ms (an uninterrupted 1024-token generation would keep Ollama busy for more than 10 s)' $follow.first_record_ms ($follow.first_record_ms -lt 1000)
    # The pitfall: Dispose() of an unread response keeps the connection open for up to 2 s by default.
    $null = Stream $body @{ CancelAfterRecords = 25; DrainOnDispose = $true }
    $drained = Stream $short
    Check 'with the default drain the next request waits for the drain (documents the pitfall)' 'first record >= 1500 ms' $drained.first_record_ms ($drained.first_record_ms -ge 1500)
    @{ Actual = [ordered]@{ baseline_total_ms = $baseline.total_ms; cancelled = Brief $r; next_first_record_ms = $follow.first_record_ms; next_total_ms = $follow.total_ms; next_first_record_ms_with_default_drain = $drained.first_record_ms }; Request = $body }
}

Run-Scenario 'S11' 'Total timeout cancels the request and frees Ollama' @('outcome timeout with kind total', 'error code AI_TIMEOUT', 'the next request completes quickly') {
    $body = New-Body $longPrompt $true $true 1024
    $r = Invoke-OllamaStream -BaseUrl $BaseUrl -Body $body -ExpectedModel $model -TotalTimeoutSec 2
    Check 'outcome is timeout' 'timeout' $r.outcome ($r.outcome -ceq 'timeout')
    Check 'timeout kind is total' 'total' $r.timeout_kind ($r.timeout_kind -ceq 'total')
    Check 'error code' 'AI_TIMEOUT' $r.error_code ($r.error_code -ceq 'AI_TIMEOUT')
    $follow = Stream (New-Body 'Say hi.' $false $true 64)
    Check 'next request completes' 'completed' $follow.outcome ($follow.outcome -ceq 'completed')
    Check 'next request is not blocked' '< 3000 ms' $follow.total_ms ($follow.total_ms -lt 3000)
    @{ Actual = [ordered]@{ timeout = Brief $r; next_request_total_ms = $follow.total_ms }; Request = $body }
}

Run-Scenario 'S12' 'Unavailable endpoint' @('connection refused', 'outcome unavailable and error code AI_UNAVAILABLE', 'fast failure') {
    $dead = [uri]'http://127.0.0.1:11499'
    $r = Invoke-OllamaStream -BaseUrl $dead -Body (New-Body 'hi' $false $true 16) -TotalTimeoutSec 10
    Check 'outcome is unavailable' 'unavailable' $r.outcome ($r.outcome -ceq 'unavailable')
    Check 'error code' 'AI_UNAVAILABLE' $r.error_code ($r.error_code -ceq 'AI_UNAVAILABLE')
    Check 'fails fast' '< 3000 ms' $r.total_ms ($r.total_ms -lt 3000)
    @{ Actual = Brief $r; Request = 'POST http://127.0.0.1:11499/api/generate (nothing listening)' }
}

Run-Scenario 'S13' 'Latency profile of streaming (warm, 5 requests)' @('all requests complete', 'first record and first answer delta times are recorded') {
    $rows = foreach ($i in 1..5) {
        $r = Stream (New-Body 'What is the capital of France? Answer briefly.')
        Check "request $i completed" 'completed' $r.outcome ($r.outcome -ceq 'completed')
        [pscustomobject]@{ first_record_ms = $r.first_record_ms; first_answer_ms = $r.first_answer_ms; total_ms = $r.total_ms; interval_mean_ms = $r.interval_mean_ms }
    }
    $sorted = { param($name) @($rows | ForEach-Object { $_.$name } | Sort-Object)[2] }
    @{ Actual = [ordered]@{ requests = $rows; median_first_record_ms = (& $sorted 'first_record_ms'); median_first_answer_ms = (& $sorted 'first_answer_ms'); median_total_ms = (& $sorted 'total_ms') }; Request = 'format + think=false, 5 requests' }
}

if ($IncludeRunnerStop) {
    Run-Scenario 'S14' 'Interrupted stream: the Ollama model runner is stopped mid-generation (failure injection)' @('the stream ends with an {"error": ...} record and no done=true', 'outcome provider_error, error code AI_PROVIDER_ERROR', 'the upstream message is sanitized (no loopback endpoint)', 'Ollama serves the next request again') {
        $body = New-Body $longPrompt $true $true 1024
        $stopRunner = {
            param($count)
            if ($count -eq 15) {
                Get-Process -Name llama-server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\Ollama\*' } | Stop-Process -Force
            }
        }
        $r = Stream $body @{ OnRecord = $stopRunner }
        Check 'outcome is provider_error' 'provider_error' $r.outcome ($r.outcome -ceq 'provider_error')
        Check 'error code' 'AI_PROVIDER_ERROR' $r.error_code ($r.error_code -ceq 'AI_PROVIDER_ERROR')
        Check 'no final done record' 'done=false' $r.done (-not $r.done)
        Check 'upstream message is present but has no loopback endpoint' 'sanitized' $r.upstream_error ($r.upstream_error -and $r.upstream_error -notmatch '127\.0\.0\.1:\d+')
        Start-Sleep -Seconds 2
        $after = Stream (New-Body 'Say hi.' $false $true 64)
        Check 'Ollama serves the next request' 'completed' $after.outcome ($after.outcome -ceq 'completed')
        @{ Actual = [ordered]@{ interrupted = Brief $r; next_request_total_ms = $after.total_ms }; Request = $body }
    }
}

$failed = @($scenarios | Where-Object { -not $_.passed })
$evidence = [ordered]@{
    tested_at = [DateTimeOffset]::Now.ToString('o'); endpoint = [uri]::new($BaseUrl, '/api/generate').AbsoluteUri
    ollama_version = $version; model = $model; model_digest = $installed[0].digest
    script_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    module_sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'OllamaStreaming.psm1') -Algorithm SHA256).Hash
    total = $scenarios.Count; passed = $scenarios.Count - $failed.Count; failed = $failed.Count
    opt_in = [ordered]@{ runner_stop = [bool]$IncludeRunnerStop; cold_start = [bool]$IncludeColdStart }
    scenarios = $scenarios.ToArray()
}
try {
    [IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath), ($evidence | ConvertTo-Json -Depth 14), [Text.UTF8Encoding]::new($false))
} catch { [Console]::Error.WriteLine("FAIL: cannot save evidence: $($_.Exception.Message)"); exit 1 }
Write-Output "SUMMARY: scenarios=$($scenarios.Count), passed=$($scenarios.Count - $failed.Count), failed=$($failed.Count)"
if ($failed.Count) { exit 1 }
exit 0
