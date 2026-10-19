<#
.SYNOPSIS
  Smoke-tests the /api/chat contract against a running BC-LocalAI instance (BE-001).
  Since BE-002 the app answers through Ollama, so Ollama (qwen3:4b) must be running for the 200 case.
  Without Ollama the 200 case fails with 503 AI_UNAVAILABLE by design; the 400 cases still pass.
  Use `dotnet test` for the Ollama-free (MockAIService) checks.
.EXAMPLE
  dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http   # terminal 1
  .\scripts\smoke-test.ps1                                                    # terminal 2
#>
param([string]$BaseUrl = "http://localhost:5290")

$failed = 0
# HttpClient works in both Windows PowerShell 5.1 and PowerShell 7+.
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()

function Test-Case($name, $body, $expectedStatus, $check) {
    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")
    $resp = $client.PostAsync("$BaseUrl/api/chat", $content).GetAwaiter().GetResult()
    $status = [int]$resp.StatusCode
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $ok = $false
    try { $ok = ($status -eq $expectedStatus) -and (& $check ($text | ConvertFrom-Json)) } catch { $ok = $false }
    if ($ok) { Write-Host "PASS  $name -> $status $text" -ForegroundColor Green }
    else { Write-Host "FAIL  $name -> $status $text" -ForegroundColor Red; $script:failed++ }
}

$isOk  = { param($j) -not [string]::IsNullOrWhiteSpace($j.response) -and -not [string]::IsNullOrWhiteSpace($j.model) }
$isErr = { param($j) $j.error.code -eq "INVALID_MESSAGE" -and $j.error.message -eq "Message is required" }

Test-Case "valid message"   '{"message":"สวัสดี AI"}' 200 $isOk
Test-Case "empty message"   '{"message":""}'          400 $isErr
Test-Case "whitespace only" '{"message":"   "}'       400 $isErr
Test-Case "missing field"   '{}'                      400 $isErr
Test-Case "malformed JSON"  '{ bad json'              400 $isErr
Test-Case "wrong type"      '{"message":123}'         400 $isErr

if ($failed -gt 0) { Write-Host "$failed case(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All smoke tests passed" -ForegroundColor Green
