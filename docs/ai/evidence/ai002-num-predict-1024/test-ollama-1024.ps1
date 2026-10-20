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
#requires -Version 7.0
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
    $version = Invoke-RestMethod -Uri "$baseUri/api/version" -NoProxy -MaximumRedirection 0 -TimeoutSec ([Math]::Min(10, $TimeoutSec))
    if ($version.version -isnot [string] -or [string]::IsNullOrWhiteSpace($version.version)) {
        throw 'The endpoint did not return a valid Ollama version.'
    }
    $stage = 'generating a response'
    $hint = 'Run: ollama pull qwen3:4b. For a slow first load, increase -TimeoutSec and check available RAM/VRAM.'
    $payload = @{
        model = 'qwen3:4b'; prompt = $Prompt; stream = $false; think = $false
        system = 'Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.'
        format = @{
            type = 'object'
            properties = @{ answer = @{ type = 'string' } }
            required = @('answer')
            additionalProperties = $false
        }
        options = @{ temperature = 0; num_predict = 1024 }
    } | ConvertTo-Json -Depth 8
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $http = Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/generate" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) `
        -NoProxy -MaximumRedirection 0 -TimeoutSec $TimeoutSec
    $timer.Stop()
    $stage = 'validating a response'
    $hint = 'The model response must finish with done_reason=stop and contain JSON with one non-empty string field: answer. Inspect runtime/model compatibility; truncated output is not a PASS.'
    # JsonDocument retains duplicate keys; ConvertFrom-Json alone collapses them.
    $document = [Text.Json.JsonDocument]::Parse([string]$http.Content)
    try {
        if ($http.StatusCode -ne 200 -or $document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'Expected HTTP 200 and a JSON object.'
        }
        $properties = @($document.RootElement.EnumerateObject())
        $fields = @{}
        foreach ($name in 'model', 'done', 'done_reason', 'response') {
            $matchingProperties = @($properties | Where-Object { $_.Name -ceq $name })
            if ($matchingProperties.Count -ne 1) { throw "Expected exactly one '$name' field." }
            $fields[$name] = $matchingProperties[0].Value
        }
        if ($fields.model.ValueKind -ne [Text.Json.JsonValueKind]::String -or $fields.model.GetString() -cne 'qwen3:4b' -or
            $fields.done.ValueKind -ne [Text.Json.JsonValueKind]::True -or
            $fields.done_reason.ValueKind -ne [Text.Json.JsonValueKind]::String -or $fields.done_reason.GetString() -cne 'stop' -or
            $fields.response.ValueKind -ne [Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($fields.response.GetString())) {
            throw 'Unexpected or truncated response: expected model qwen3:4b, done=true, done_reason=stop and a non-empty response string.'
        }
        $inner = [Text.Json.JsonDocument]::Parse($fields.response.GetString())
        try {
            if ($inner.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Expected an answer object.' }
            $answerProperties = @($inner.RootElement.EnumerateObject())
            if ($answerProperties.Count -ne 1 -or $answerProperties[0].Name -cne 'answer' -or
                $answerProperties[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                throw 'Expected exactly one string field named answer.'
            }
            $answerText = $answerProperties[0].Value.GetString()
            if ([string]::IsNullOrWhiteSpace($answerText) -or $answerText -match '</?think>') {
                throw 'Expected a non-empty final answer without thinking markers.'
            }
        } finally { $inner.Dispose() }
    } finally { $document.Dispose() }
    $result = $http.Content | ConvertFrom-Json
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
    Write-Output $answerText
    exit 0
} catch {
    [Console]::Error.WriteLine("FAIL while ${stage} at ${baseUri}: $($_.Exception.Message)")
    [Console]::Error.WriteLine($hint)
    exit 1
}
