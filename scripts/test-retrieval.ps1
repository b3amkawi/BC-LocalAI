#requires -Version 7.0
<# Offline AI-010 convenience launcher. Does not call Ollama or use the GPU. #>
[CmdletBinding()]
param(
    [ValidateSet('validate', 'prepare', 'grade')][string]$Command = 'validate',
    [string]$OutputPath,
    [string]$ResponsesPath,
    [ValidateRange(2, 2000000)][int]$MaxContextBytes = 8192,
    [ValidateRange(1, 20)][int]$TopK = 4
)

$scriptPath = Join-Path $PSScriptRoot 'test-retrieval.py'
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python -and $python.Source -like '*WindowsApps*') { $python = $null }
$launcher = if (-not $python) { Get-Command py -ErrorAction SilentlyContinue } else { $null }
$pythonCommand = if ($python) { $python.Source } elseif ($launcher) { $launcher.Source } else { $null }
if (-not $pythonCommand) {
    Write-Error 'Python 3.11+ is required. Install Python using the approved company process, then reopen PowerShell.'
    exit 2
}
$arguments = @()
if ($launcher) { $arguments += '-3' }
$arguments += @('-X', 'utf8', $scriptPath, $Command, '--max-context-bytes', "$MaxContextBytes", '--top-k', "$TopK")
if ($OutputPath) { $arguments += @('--output', $OutputPath) }
if ($ResponsesPath) { $arguments += @('--responses', $ResponsesPath) }
& $pythonCommand @arguments
exit $LASTEXITCODE
