<#
.SYNOPSIS
Opens a simple interactive Thai chat with local qwen3:4b through Ollama.
.DESCRIPTION
Uses only http://127.0.0.1:11434 and prints the final structured answer.
Enter /bye to exit.
#>
#requires -Version 7.0
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

        $http = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post `
            -ContentType 'application/json; charset=utf-8' `
            -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -NoProxy -MaximumRedirection 0 -TimeoutSec $TimeoutSec

        # Validate before printing; JsonDocument preserves duplicate field names.
        $document = [Text.Json.JsonDocument]::Parse([string]$http.Content)
        try {
            if ($http.StatusCode -ne 200 -or $document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                throw 'ต้องได้รับ HTTP 200 และ JSON object จาก Ollama'
            }
            $properties = @($document.RootElement.EnumerateObject())
            $fields = @{}
            foreach ($name in 'model', 'done', 'done_reason', 'response') {
                $matchingProperties = @($properties | Where-Object { $_.Name -ceq $name })
                if ($matchingProperties.Count -ne 1) { throw "Ollama ต้องมี field '$name' เพียงครั้งเดียว" }
                $fields[$name] = $matchingProperties[0].Value
            }
            if ($fields.model.ValueKind -ne [Text.Json.JsonValueKind]::String -or $fields.model.GetString() -cne 'qwen3:4b' -or
                $fields.done.ValueKind -ne [Text.Json.JsonValueKind]::True -or
                $fields.done_reason.ValueKind -ne [Text.Json.JsonValueKind]::String -or $fields.done_reason.GetString() -cne 'stop' -or
                $fields.response.ValueKind -ne [Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($fields.response.GetString())) {
                throw 'โมเดลหรือรูปแบบคำตอบไม่ตรง qwen3:4b หรือคำตอบยังไม่สมบูรณ์'
            }
            $inner = [Text.Json.JsonDocument]::Parse($fields.response.GetString())
            try {
                if ($inner.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'คำตอบต้องเป็น JSON object' }
                $answerProperties = @($inner.RootElement.EnumerateObject())
                if ($answerProperties.Count -ne 1 -or $answerProperties[0].Name -cne 'answer' -or
                    $answerProperties[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw 'ต้องมี answer เป็น string เพียง field เดียว'
                }
                $answerText = $answerProperties[0].Value.GetString()
                if ([string]::IsNullOrWhiteSpace($answerText) -or $answerText -match '</?think>') {
                    throw 'ไม่พบคำตอบสุดท้ายที่ใช้งานได้ หรือมี Thinking marker ปะปน'
                }
            } finally { $inner.Dispose() }
        } finally { $document.Dispose() }
        Write-Host "AI > $answerText" -ForegroundColor Cyan
    } catch {
        Write-Host "ผิดพลาด > $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'ตรวจว่า Ollama เปิดอยู่และติดตั้ง qwen3:4b แล้ว' -ForegroundColor DarkYellow
    }
}

Write-Host 'ปิด Qwen3 Local AI แล้ว' -ForegroundColor DarkGray
