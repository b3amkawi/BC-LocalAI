#requires -Version 7.0
# Test-only sampler: an in-window sample is published during shutdown, never before it.
param([uri]$BaseUrl, [string]$OutputCsv, [string]$OutputJson)
$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot '../../scripts/benchmark/BenchmarkCommon.psm1') -Force -PassThru
Assert-LoopbackBaseUrl $BaseUrl -RejectOllamaPort
& $module {
    function script:Start-ResourceSampler([int]$IntervalMs) {
        $state = [pscustomobject]@{ Samples = [Collections.Concurrent.ConcurrentQueue[object]]::new() }
        $state.Samples.Enqueue([pscustomobject]@{
            time_utc = [DateTimeOffset]::UtcNow.AddHours(-1); cpu_percent = 99; ram_available_mb = 1
            ollama_working_set_mb = 99; gpu_percent = 99; vram_used_mb = 99
        })
        return $state
    }
    function script:Stop-ResourceSampler($Sampler) {
        $Sampler.Samples.Enqueue([pscustomobject]@{
            time_utc = [DateTimeOffset]::UtcNow.AddMilliseconds(-100); cpu_percent = 25; ram_available_mb = 2048
            ollama_working_set_mb = 512; gpu_percent = 10; vram_used_mb = 128
        })
        $Sampler.Samples.Enqueue([pscustomobject]@{
            time_utc = [DateTimeOffset]::UtcNow.AddHours(1); cpu_percent = 99; ram_available_mb = 1
            ollama_working_set_mb = 99; gpu_percent = 99; vram_used_mb = 99
        })
        return @($Sampler.Samples.ToArray())
    }
}
$newBody = { param([string]$Prompt) @{ message = $Prompt } | ConvertTo-Json -Compress }
$null = Invoke-ChatBenchmark -Target backend -Endpoint ([uri]::new($BaseUrl, '/api/chat')) -NewBody $newBody `
    -Concurrency @(2) -Profiles @('short') -Rounds 3 -WarmupRequests 0 -TimeoutSec 10 `
    -ResourceIntervalMs 200 -MinAvailableMemoryMB 0 -OutputCsv $OutputCsv -OutputJson $OutputJson -Environment @{}
