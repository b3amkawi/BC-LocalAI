<#
.SYNOPSIS
AI-006: logs which processes use the GPU while a benchmark runs (read-only, no changes).
.DESCRIPTION
Every -IntervalSec seconds appends one JSON line with the total GPU utilisation, VRAM use, and the processes
with the highest 3D utilisation and dedicated VRAM. Used to tell how much of a benchmark's GPU time was taken
by other programs on the same machine. Stop it with Ctrl+C or -DurationSec.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(2, 600)][int]$IntervalSec = 15,
    [ValidateRange(1, 86400)][int]$DurationSec = 86400
)
$ErrorActionPreference = 'Continue'
$end = [DateTime]::UtcNow.AddSeconds($DurationSec)
function Get-Name([string]$Instance) { if ($Instance -match 'pid_(\d+)') { (Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue).ProcessName } }
while ([DateTime]::UtcNow -lt $end) {
    $total = (& nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits 2>$null)
    $util = @{}
    (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples | Where-Object { $_.CookedValue -gt 0.5 } | ForEach-Object {
        $n = Get-Name $_.InstanceName; if ($n) { $util[$n] = [math]::Round(($util[$n] + $_.CookedValue), 1) }
    }
    $mem = @{}
    (Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | Where-Object { $_.CookedValue -gt 100MB } | ForEach-Object {
        $n = Get-Name $_.InstanceName; if ($n) { $mem[$n] = [int](($mem[$n] + $_.CookedValue) / 1MB) }
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $line = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); gpu_total = $total; free_ram_mb = [int]($os.FreePhysicalMemory / 1024); gpu_3d_by_process = $util; vram_mb_by_process = $mem }
    Add-Content -LiteralPath $OutputPath -Value ($line | ConvertTo-Json -Compress -Depth 4)
    Start-Sleep -Seconds $IntervalSec
}
