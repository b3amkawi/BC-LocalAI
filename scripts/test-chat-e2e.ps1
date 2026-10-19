<#
.SYNOPSIS
Evaluates AI-003 through Backend POST /api/chat only (40 cases per category).
.DESCRIPTION
Requires PowerShell 7. Never calls Ollama directly. No redirects are followed.
Returns exit 0 only if every case passes; any failure returns exit 1.
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-chat-e2e.ps1
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://localhost:5290',
    [string]$DatasetPath = (Join-Path $PSScriptRoot '..\docs\ai\chat-evaluation-cases.json'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\ai\chat-evaluation-evidence.json'),
    [ValidateRange(1, 1800)][int]$TimeoutSec = 300,
    [string]$ColdStartNote = ''
)

$ErrorActionPreference = 'Stop'
function Fail([string]$Message) {
    [Console]::Error.WriteLine("FAIL: $Message")
    exit 1
}

if (-not $BaseUrl.IsAbsoluteUri -or -not $BaseUrl.IsLoopback -or
    $BaseUrl.Scheme -notin @('http', 'https') -or $BaseUrl.Port -eq 11434 -or
    $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment -or $BaseUrl.AbsolutePath -ne '/') {
    Fail 'BaseUrl must be a loopback HTTP(S) Backend origin (for example http://localhost:5290), not the Ollama port.'
}

try {
    $datasetText = Get-Content -Raw -LiteralPath $DatasetPath -Encoding utf8
    $dataset = ConvertFrom-Json -InputObject $datasetText -AsHashtable -NoEnumerate
    if ($dataset -isnot [Collections.IDictionary] -or $dataset.cases -isnot [array]) {
        throw 'Dataset must be an object containing a cases array.'
    }
    $cases = @($dataset.cases)
    foreach ($category in 'thai', 'english', 'math', 'edge') {
        if (@($cases | Where-Object { $_.category -ceq $category }).Count -lt 40) {
            throw "Dataset needs at least 40 cases in category '$category'."
        }
    }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $cases) {
        if ($case -isnot [Collections.IDictionary] -or $case.id -isnot [string] -or
            [string]::IsNullOrWhiteSpace($case.id) -or -not $ids.Add($case.id) -or
            $case.category -cnotin @('thai', 'english', 'math', 'edge') -or
            $case.language -cnotin @('th', 'en', 'any') -or $case.prompt -isnot [string] -or
            [string]::IsNullOrWhiteSpace($case.prompt) -or $case.expected_regex -isnot [string] -or
            [string]::IsNullOrWhiteSpace($case.expected_regex)) {
            throw 'Every case needs a unique non-empty id, known category/language, prompt and expected_regex.'
        }
        if (($case.category -ceq 'thai' -and $case.language -cne 'th') -or
            ($case.category -ceq 'english' -and $case.language -cne 'en')) {
            throw 'Thai/English categories must declare their corresponding language.'
        }
        $null = [regex]::new($case.expected_regex, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromSeconds(1))
        if ($case.Contains('forbid_regex')) {
            if ($case.forbid_regex -isnot [string] -or [string]::IsNullOrWhiteSpace($case.forbid_regex)) {
                throw 'forbid_regex must be a non-empty regex when supplied.'
            }
            $null = [regex]::new($case.forbid_regex, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromSeconds(1))
        }
    }
    $destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) {
        throw 'Output directory must already exist.'
    }
} catch { Fail "Invalid dataset or evidence path: $($_.Exception.Message)" }

$results = [Collections.Generic.List[object]]::new()
$endpoint = [uri]::new($BaseUrl, '/api/chat')
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
try {
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $errors = [Collections.Generic.List[string]]::new()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $status = $null; $answer = $null; $model = $null; $raw = $null
        $content = $null; $http = $null; $document = $null
        try {
            $payload = @{ message = $case.prompt } | ConvertTo-Json
            $content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
            $http = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
            $raw = $http.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $status = [int]$http.StatusCode
            if ($status -ne 200) { $errors.Add("HTTP status is $status") }
            try {
                $document = [Text.Json.JsonDocument]::Parse($raw)
                if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                    throw 'response body must be a JSON object'
                }
                $properties = @($document.RootElement.EnumerateObject())
                foreach ($field in 'response', 'model') {
                    $property = @($properties | Where-Object { $_.Name -ceq $field })
                    if ($property.Count -ne 1 -or $property[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                        $errors.Add("$field must appear exactly once as a string")
                    } elseif ($field -ceq 'response') {
                        $answer = $property[0].Value.GetString()
                    } else { $model = $property[0].Value.GetString() }
                }
            } catch { $errors.Add("invalid Backend JSON: $($_.Exception.Message)") }
            if ($model -cne 'qwen3:4b') { $errors.Add("model is '$model', expected qwen3:4b") }
            if ([string]::IsNullOrWhiteSpace($answer)) { $errors.Add('response is not a non-empty string') }
            else {
                if ($answer -match '^\s*(?:```(?:json)?\s*)?[\{\[]') { $errors.Add('response contains nested JSON') }
                # Detect a JSON string wrapping a JSON object/array as well.
                if ($answer.TrimStart().StartsWith('"')) {
                    try {
                        $inner = ConvertFrom-Json -InputObject $answer -NoEnumerate
                        if ($inner -is [string] -and $inner -match '^\s*[\{\[]') { $errors.Add('response contains nested JSON') }
                    } catch { } # Quoted natural language need not be JSON.
                }
                if ($answer -match '(?i)</?think>') { $errors.Add('response contains a thinking marker') }
                if ($answer -match '(?i)(stack trace|\bSystem\.[A-Za-z.]*Exception\b| at [A-Za-z0-9_.]+\([^)]*\)|connection string|api[_ -]?key|[A-Z]:\\(?:Users|Windows)\\|/home/[^\s/]+/)') {
                    $errors.Add('response contains possible stack trace or system details')
                }
                $thaiCount = [regex]::Matches($answer, '[\u0E01-\u0E2E\u0E30-\u0E4E]').Count
                $latinCount = [regex]::Matches($answer, '[A-Za-z]').Count
                if ($case.language -ceq 'th' -and ($thaiCount -eq 0 -or $thaiCount -lt $latinCount)) { $errors.Add('response language is not predominantly Thai') }
                if ($case.language -ceq 'en' -and ($thaiCount -gt 0 -or $latinCount -eq 0)) { $errors.Add('response language is not English') }
                if (-not [regex]::new($case.expected_regex, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromSeconds(1)).IsMatch($answer)) {
                    $errors.Add("response does not match expected_regex: $($case.expected_regex)")
                }
                if ($case.Contains('forbid_regex') -and [regex]::new($case.forbid_regex, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromSeconds(1)).IsMatch($answer)) {
                    $errors.Add("response matches forbid_regex: $($case.forbid_regex)")
                }
            }
        } catch { $errors.Add("request failed: $($_.Exception.Message)") }
        finally {
            $timer.Stop()
            if ($document) { $document.Dispose() }
            if ($http) { $http.Dispose() }
            if ($content) { $content.Dispose() }
        }
        $passed = $errors.Count -eq 0
        $phase = if ($index -gt 0) { 'warm' } elseif ($ColdStartNote) { 'cold' } else { 'cold_candidate' }
        $results.Add([ordered]@{
            id = $case.id; category = $case.category; language = $case.language; phase = $phase
            prompt = $case.prompt; expected_regex = $case.expected_regex
            http_status = $status; elapsed_ms = $timer.ElapsedMilliseconds
            response = $answer; model = $model; raw_body = $raw; passed = $passed; errors = $errors.ToArray()
        })
        Write-Output ("{0} {1}: HTTP {2}; {3} ms" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $case.id, $status, $timer.ElapsedMilliseconds)
        if (-not $passed) { $errors | ForEach-Object { Write-Output "  $_" } }
    }
} finally { $client.Dispose() }

$warm = @($results | Where-Object { $_['phase'] -ceq 'warm' -and $_['passed'] } | ForEach-Object { $_['elapsed_ms'] } | Sort-Object)
$warmAverage = if ($warm.Count) { [math]::Round(($warm | Measure-Object -Average).Average, 1) } else { $null }
$warmP95 = if ($warm.Count) { $warm[[int][math]::Ceiling($warm.Count * 0.95) - 1] } else { $null }
$evidence = [ordered]@{
    tested_at = [DateTimeOffset]::Now.ToString('o'); endpoint = $endpoint.AbsoluteUri
    dataset_sha256 = (Get-FileHash -LiteralPath $DatasetPath -Algorithm SHA256).Hash
    evaluator_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    total = $results.Count; passed = @($results | Where-Object { $_['passed'] }).Count
    failed = @($results | Where-Object { -not $_['passed'] }).Count
    cold_start_note = $ColdStartNote; cold_candidate_ms = $results[0]['elapsed_ms']
    warm_sample_count = $warm.Count; warm_average_ms = $warmAverage; warm_p95_ms = $warmP95
    results = $results.ToArray()
}
$temporary = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.chat-evaluation-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $destination, $true)
} catch { Fail "Cannot save evidence: $($_.Exception.Message)" }
finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
Write-Output "SUMMARY: total=$($evidence.total), passed=$($evidence.passed), failed=$($evidence.failed), first=$($evidence.cold_candidate_ms) ms, warm_average=$($evidence.warm_average_ms) ms, warm_p95=$($evidence.warm_p95_ms) ms"
if ($evidence.failed -gt 0) { exit 1 }
exit 0
