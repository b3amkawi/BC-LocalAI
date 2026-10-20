<#
.SYNOPSIS
  LIVE smoke test for BE-002: POST /api/chat answered by a real Ollama (qwen3:4b).
  Requires Ollama running with the model installed. Not part of `dotnet test`.
.EXAMPLE
  ollama pull qwen3:4b                                                        # once
  dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http   # terminal 1
  .\scripts\live-smoke-test.ps1                                               # terminal 2
.NOTES
  Exit code 0 = all checks passed, 1 = at least one check failed.
  Works on Windows PowerShell 5.1 and PowerShell 7+.
#>
param(
    [string]$BaseUrl = "http://localhost:5290",
    [string]$ExpectedModel = "qwen3:4b",
    [int]$TimeoutSeconds = 300
)

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$failed = 0

function Send-Chat([string]$message) {
    $body = (@{ message = $message } | ConvertTo-Json -Compress)
    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")
    $resp = $client.PostAsync("$BaseUrl/api/chat", $content).GetAwaiter().GetResult()
    [pscustomobject]@{
        Status = [int]$resp.StatusCode
        Text   = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "PASS  $name  $detail" -ForegroundColor Green }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failed++ }
}

function Test-Answer([string]$name, [string]$message, [string[]]$mustContainAny) {
    $r = Send-Chat $message
    $json = $null
    try { $json = $r.Text | ConvertFrom-Json } catch { }
    $answer = if ($json) { [string]$json.response } else { "" }
    $ok = ($r.Status -eq 200) -and $json -and ($json.model -eq $ExpectedModel) `
        -and ($answer.Trim().Length -gt 0) -and (-not $answer.TrimStart().StartsWith("{")) `
        -and (-not $answer.Contains("<think>")) -and (-not $answer.Contains("</think>")) `
        -and ($mustContainAny.Count -eq 0 -or @($mustContainAny | Where-Object { $answer.Contains($_) }).Count -gt 0)
    Check $name $ok "-> $($r.Status) $($r.Text)"
}

try {
    Test-Answer "thai calculation"    "หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ" @("2", "สอง")
    Test-Answer "english calculation" "What is 2 + 3? Answer briefly."    @("5", "five")
    Test-Answer "thai question"       "เมืองหลวงของประเทศไทยคืออะไร ตอบสั้น ๆ" @("กรุงเทพ", "Bangkok")
    # BE-003: a question that makes the model write a lot used to fail with 502 AI_INVALID_RESPONSE (output limit 256).
    Test-Answer "long answer (BE-003)" "ตอบให้ยาวที่สุดเท่าที่จะทำได้ อธิบายประวัติศาสตร์กรุงเทพมหานครทุกยุคทุกสมัยอย่างละเอียดมาก แบ่งเป็นหัวข้อย่อย อย่างน้อย 20 หัวข้อ" @()

    $empty = Send-Chat ""
    $emptyOk = $empty.Status -eq 400 -and ($empty.Text | ConvertFrom-Json).error.code -eq "INVALID_MESSAGE"
    Check "empty message -> 400" $emptyOk "-> $($empty.Status) $($empty.Text)"
}
catch {
    Write-Host "FAIL  could not reach $BaseUrl : $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}

if ($failed -gt 0) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All live smoke tests passed" -ForegroundColor Green
exit 0
