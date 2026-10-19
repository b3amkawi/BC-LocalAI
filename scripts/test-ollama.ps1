<#
.SYNOPSIS
Tests the local Ollama qwen3:4b API and optionally saves JSON evidence.
.DESCRIPTION
Uses loopback only. Requires Ollama and qwen3:4b already installed.
Exit 0 means a complete structured answer; connection, generation, validation
and evidence-write failures return exit 1. The output directory must exist.
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama.ps1
.EXAMPLE
pwsh -NoProfile -File .\scripts\test-ollama.ps1 -Port 11435
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$Port = 11434,
    [ValidateNotNullOrEmpty()][string]$Prompt = 'สวัสดี',
    [ValidateRange(1, 1800)][int]$TimeoutSec = 180,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
# Keep this smoke test on loopback; never expose the Ollama listener.
$baseUri = "http://127.0.0.1:$Port"
$stage = 'checking Ollama availability'
$hint = 'Start Ollama and check GET /api/version on the selected port.'
try {
    $version = Invoke-RestMethod -Uri "$baseUri/api/version" -TimeoutSec ([Math]::Min(10, $TimeoutSec))
    if ($version.version -isnot [string] -or [string]::IsNullOrWhiteSpace($version.version)) {
        throw 'The endpoint did not return a valid Ollama version.'
    }
    $stage = 'generating a response'
    $hint = 'Run: ollama pull qwen3:4b. For a slow first load, increase -TimeoutSec and check available RAM/VRAM.'
    $payload = @{
        model = 'qwen3:4b'; prompt = $Prompt; stream = $false; think = $false
        system = 'Answer the user briefly in Thai. Return a JSON object with an answer field containing only your final answer.'
        format = @{
            type = 'object'
            properties = @{ answer = @{ type = 'string' } }
            required = @('answer')
            additionalProperties = $false
        }
        options = @{ temperature = 0; num_predict = 256 }
    } | ConvertTo-Json -Depth 8
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $http = Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/generate" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) `
        -TimeoutSec $TimeoutSec
    $timer.Stop()
    $stage = 'validating a response'
    $hint = 'The model response must finish with done_reason=stop and contain JSON with one non-empty string field: answer. Inspect runtime/model compatibility; truncated output is not a PASS.'
    $result = $http.Content | ConvertFrom-Json
    if ($http.StatusCode -ne 200 -or -not $http.Content.TrimStart().StartsWith('{') -or
        $result -isnot [pscustomobject] -or $result.done -isnot [bool] -or $result.done -ne $true -or
        $result.response -isnot [string] -or [string]::IsNullOrWhiteSpace($result.response) -or
        $result.model -isnot [string] -or $result.model -cne 'qwen3:4b' -or
        $result.done_reason -isnot [string] -or $result.done_reason -cne 'stop') {
        throw 'Unexpected or truncated response: expected HTTP 200, model qwen3:4b, done=true, done_reason=stop and a non-empty response.'
    }
    $answer = $result.response | ConvertFrom-Json
    if (-not $result.response.TrimStart().StartsWith('{') -or
        $answer -isnot [pscustomobject] -or @($answer.PSObject.Properties).Count -ne 1 -or
        @($answer.PSObject.Properties.Name)[0] -cne 'answer' -or
        $answer.answer -isnot [string] -or [string]::IsNullOrWhiteSpace($answer.answer) -or
        $answer.answer -match '</?think>') {
        throw 'Expected structured JSON with a non-empty final answer and no thinking markers.'
    }
    $evidence = [ordered]@{
        tested_at = [DateTimeOffset]::Now.ToString('o')
        endpoint = "$baseUri/api/generate"
        ollama_version = $version.version
        http_status = [int]$http.StatusCode
        elapsed_ms = $timer.ElapsedMilliseconds
        request = ($payload | ConvertFrom-Json)
        result = $result
    }
    if ($OutputPath) {
        $stage = 'saving evidence'
        $hint = 'Choose a writable -OutputPath in an existing directory. API success alone does not mean the evidence was saved.'
        $json = $evidence | ConvertTo-Json -Depth 20
        $destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
        $temporary = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.ollama-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
            if ([IO.File]::Exists($destination)) {
                [IO.File]::Replace($temporary, $destination, [NullString]::Value)
            } else {
                [IO.File]::Move($temporary, $destination)
            }
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    }
    Write-Output "PASS: HTTP 200; qwen3:4b; done=true; done_reason=stop; $($timer.ElapsedMilliseconds) ms"
    Write-Output $answer.answer
    exit 0
} catch {
    [Console]::Error.WriteLine("FAIL while ${stage} at ${baseUri}: $($_.Exception.Message)")
    [Console]::Error.WriteLine($hint)
    exit 1
}
