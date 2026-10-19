<#
.SYNOPSIS
Runs the AI-003 dataset through the Backend POST /api/chat endpoint.
.DESCRIPTION
Calls only a loopback Backend URL. It never calls Ollama directly. Exit 0 means
all cases passed; dataset, connection, HTTP, contract, quality, or evidence-write
failures return exit 1. The first request is reported as the cold-start candidate;
remaining requests are warm requests.
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://localhost:5290',
    [string]$DatasetPath = '.\docs\ai\chat-evaluation-cases.json',
    [string]$OutputPath = '.\docs\ai\chat-evaluation-evidence.json',
    [ValidateRange(1, 1800)][int]$TimeoutSec = 300
)

$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()

function Fail([string]$Message) {
    [Console]::Error.WriteLine("FAIL: $Message")
    exit 1
}

if (-not $BaseUrl.IsAbsoluteUri -or -not $BaseUrl.IsLoopback) {
    Fail 'BaseUrl must be an absolute loopback URL such as http://localhost:5290.'
}

try {
    $dataset = Get-Content -Raw -LiteralPath $DatasetPath -Encoding utf8 | ConvertFrom-Json
} catch {
    Fail "Cannot read dataset '$DatasetPath': $($_.Exception.Message)"
}

$cases = @($dataset.cases)
$counts = @{}
foreach ($name in 'thai', 'english', 'math', 'edge') {
    $counts[$name] = @($cases | Where-Object category -CEQ $name).Count
}
if ($cases.Count -lt 20 -or $counts.thai -lt 5 -or $counts.english -lt 5 -or
    $counts.math -lt 5 -or $counts.edge -lt 5) {
    Fail "Dataset minimum not met: total=$($cases.Count), thai=$($counts.thai), english=$($counts.english), math=$($counts.math), edge=$($counts.edge)."
}
if (@($cases.id | Sort-Object -Unique).Count -ne $cases.Count) {
    Fail 'Dataset case IDs must be unique.'
}

$endpoint = [uri]::new($BaseUrl, '/api/chat')
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)

try {
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $errors = [Collections.Generic.List[string]]::new()
        if ($case.prompt -isnot [string] -or [string]::IsNullOrWhiteSpace($case.prompt)) {
            $errors.Add('prompt is empty')
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $status = $null
        $answer = $null
        $model = $null
        try {
            $payload = @{ message = $case.prompt } | ConvertTo-Json
            $content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
            $http = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
            $raw = $http.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $status = [int]$http.StatusCode
            if ($status -ne 200) { $errors.Add("HTTP status is $status") }
            try { $body = $raw | ConvertFrom-Json } catch { $body = $null; $errors.Add('response body is not valid JSON') }
            if ($body) {
                $answer = $body.response
                $model = $body.model
                if ($answer -isnot [string] -or [string]::IsNullOrWhiteSpace($answer)) { $errors.Add('response is not a non-empty string') }
                if ($model -isnot [string] -or $model -cne 'qwen3:4b') { $errors.Add("model is '$model', expected qwen3:4b") }
                if ($answer -is [string]) {
                    if ($answer.TrimStart().StartsWith('{')) { $errors.Add('response starts with a JSON object') }
                    if ($answer -match '(?i)</?think>') { $errors.Add('response contains a thinking marker') }
                    if ($answer -match '(?i)(stack trace|system\.exception| at [A-Za-z0-9_.]+\([^)]*\)|connection string|api[_ -]?key)') { $errors.Add('response contains possible stack trace or system details') }
                    if ($case.language -ceq 'th' -and $answer -notmatch '[ก-๙]') { $errors.Add('response does not contain Thai text') }
                    if ($case.language -ceq 'en' -and ($answer -match '[ก-๙]' -or $answer -notmatch '[A-Za-z]')) { $errors.Add('response language is not English') }
                    if ($case.expected_regex -and $answer -notmatch $case.expected_regex) { $errors.Add("response does not match expected_regex: $($case.expected_regex)") }
                    if ($case.forbid_regex -and $answer -match $case.forbid_regex) { $errors.Add("response matches forbid_regex: $($case.forbid_regex)") }
                }
            }
        } catch {
            $errors.Add("request failed: $($_.Exception.Message)")
        } finally {
            $timer.Stop()
        }
        $passed = $errors.Count -eq 0
        $results.Add([ordered]@{
            id = $case.id; category = $case.category; phase = $(if ($index -eq 0) { 'cold_candidate' } else { 'warm' })
            prompt = $case.prompt; http_status = $status; elapsed_ms = $timer.ElapsedMilliseconds
            response = $answer; model = $model; passed = $passed; errors = @($errors)
        })
        Write-Output ("{0} {1}: {2} ms" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $case.id, $timer.ElapsedMilliseconds)
    }
} finally {
    $client.Dispose()
}

$warm = @($results | Where-Object { $_['phase'] -ceq 'warm' } | ForEach-Object { $_['elapsed_ms'] } | Sort-Object)
$warmAverage = if ($warm.Count) { [math]::Round(($warm | Measure-Object -Average).Average, 1) } else { $null }
$warmP95 = if ($warm.Count) { $warm[[math]::Min($warm.Count - 1, [math]::Ceiling($warm.Count * 0.95) - 1)] } else { $null }
$evidence = [ordered]@{
    tested_at = [DateTimeOffset]::Now.ToString('o'); endpoint = $endpoint.AbsoluteUri
    total = $results.Count; passed = @($results | Where-Object { $_['passed'] }).Count; failed = @($results | Where-Object { -not $_['passed'] }).Count
    cold_candidate_ms = $results[0]['elapsed_ms']; warm_average_ms = $warmAverage; warm_p95_ms = $warmP95
    results = @($results)
}
try {
    $destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $directory = [IO.Path]::GetDirectoryName($destination)
    if (-not [IO.Directory]::Exists($directory)) { throw "Output directory does not exist: $directory" }
    [IO.File]::WriteAllText($destination, ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
} catch {
    Fail "Cannot save evidence: $($_.Exception.Message)"
}

Write-Output "SUMMARY: total=$($evidence.total), passed=$($evidence.passed), failed=$($evidence.failed), cold_candidate=$($evidence.cold_candidate_ms) ms, warm_average=$($evidence.warm_average_ms) ms, warm_p95=$($evidence.warm_p95_ms) ms"
if ($evidence.failed -gt 0) { exit 1 }
exit 0
