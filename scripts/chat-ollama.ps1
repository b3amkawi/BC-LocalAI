<#
.SYNOPSIS
Opens a simple interactive Thai chat with local qwen3:4b through Ollama.
.DESCRIPTION
Uses only http://127.0.0.1:11434 and prints the final structured answer.
Enter /bye to exit.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$Port = 11434,
    [ValidateRange(1, 1800)][int]$TimeoutSec = 180
)

$ErrorActionPreference = 'Stop'
$endpoint = "http://127.0.0.1:$Port/api/generate"
$schema = @{
    type = 'object'
    properties = @{ answer = @{ type = 'string' } }
    required = @('answer')
    additionalProperties = $false
}

Write-Host 'Qwen3 Local AI พร้อมใช้งาน' -ForegroundColor Green
Write-Host 'พิมพ์คำถามแล้วกด Enter หรือพิมพ์ /bye เพื่อออก' -ForegroundColor DarkGray

while ($true) {
    $prompt = Read-Host "`nคุณ"
    if ($prompt -eq '/bye') { break }
    if ([string]::IsNullOrWhiteSpace($prompt)) { continue }

    try {
        $payload = @{
            model = 'qwen3:4b'
            prompt = $prompt
            stream = $false
            think = $false
            system = 'ตอบผู้ใช้ให้ตรงคำถาม กระชับ และใช้ภาษาเดียวกับผู้ใช้ Return a JSON object with an answer field containing only your final answer.'
            format = $schema
            options = @{ temperature = 0.2; num_predict = 512 }
        } | ConvertTo-Json -Depth 8

        $result = Invoke-RestMethod -Uri $endpoint -Method Post `
            -ContentType 'application/json; charset=utf-8' `
            -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec $TimeoutSec

        if ($result.done -ne $true -or $result.done_reason -ne 'stop' -or
            $result.response -isnot [string]) {
            throw 'โมเดลตอบไม่สมบูรณ์ กรุณาลองคำถามที่สั้นลง'
        }
        $structured = $result.response | ConvertFrom-Json
        if ($structured.answer -isnot [string] -or [string]::IsNullOrWhiteSpace($structured.answer)) {
            throw 'ไม่พบคำตอบที่ใช้งานได้จากโมเดล'
        }
        Write-Host "AI > $($structured.answer)" -ForegroundColor Cyan
    } catch {
        Write-Host "ผิดพลาด > $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'ตรวจว่า Ollama เปิดอยู่และติดตั้ง qwen3:4b แล้ว' -ForegroundColor DarkYellow
    }
}

Write-Host 'ปิด Qwen3 Local AI แล้ว' -ForegroundColor DarkGray
