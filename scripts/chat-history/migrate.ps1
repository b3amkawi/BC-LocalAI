#Requires -Version 7
# BE-007 chat history migration 001 (database/chat-history). Operator tool: never run by the application.
#   Status : shows whether the two tables exist.
#   Up     : creates them; refuses when either already exists (no IF NOT EXISTS hiding a different schema).
#   Down   : empty pre-go-live schema only, with -ConfirmEmptyRollback; the SQL guard refuses non-empty tables.
# -DefaultsFile is a MySQL option file of the MIGRATOR account (never the runtime account). It is passed to the client
# as a file, so no secret appears on a command line or in output. -ApprovedTarget must equal host:port/database of
# that file and -Database, so a wrong option file cannot silently migrate another server.
param(
    [Parameter(Mandatory)][ValidateSet('Status', 'Up', 'Down')][string]$Action,
    [Parameter(Mandatory)][string]$DefaultsFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]{1,64}$')][string]$Database,
    [Parameter(Mandatory)][string]$ApprovedTarget,
    [switch]$ConfirmEmptyRollback,
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$client = Join-Path $MySqlBin 'mysql.exe'
$up = Join-Path $repo 'database/chat-history/001-initial.up.sql'
$down = Join-Path $repo 'database/chat-history/001-initial.down.sql'
if (-not (Test-Path -LiteralPath $DefaultsFile)) { throw 'Defaults file not found.' }

$fields = @{}
foreach ($line in Get-Content -LiteralPath $DefaultsFile) {
    if ($line -match '^\s*(host|port)\s*=\s*(.+?)\s*$') { $fields[$Matches[1]] = $Matches[2] }
}
$target = "$($fields.host):$($fields.port)/$Database"
if ($target -ne $ApprovedTarget) { throw "Target $target is not the approved target. Nothing was changed." }

function Invoke-Sql([string]$Sql) {
    # Through stdin (batch mode), so client commands such as source and DELIMITER work as in a script file.
    $output = $Sql | & $client "--defaults-file=$DefaultsFile" "--database=$Database" --batch --skip-column-names --ssl-mode=REQUIRED 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MySQL client failed: $(($output | Out-String).Trim())" }
    return ($output | Out-String).Trim()
}

function Get-Tables {
    Invoke-Sql "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('Conversations', 'Messages');"
}

switch ($Action) {
    'Status' {
        Write-Output "Target $target : chat history tables present = $(Get-Tables) of 2."
    }
    'Up' {
        if ((Get-Tables) -ne '0') { throw 'Chat history tables already exist (fully or partly). Up refused; reconcile manually.' }
        Invoke-Sql "source $($up.Replace('\', '/'))`n" | Out-Null
        if ((Get-Tables) -ne '2') { throw 'Up did not create both tables; record the partial state and reconcile. Do not retry blindly.' }
        Write-Output "Applied migration 001 to $target (2 tables)."
    }
    'Down' {
        if (-not $ConfirmEmptyRollback) { throw 'Down needs -ConfirmEmptyRollback (empty pre-go-live schema only). Nothing was changed.' }
        # Same connection: the guard in the SQL reads this session variable and refuses non-empty tables.
        try {
            Invoke-Sql "SET @BE007_ALLOW_EMPTY_ROLLBACK = 1;`nsource $($down.Replace('\', '/'))`n" | Out-Null
        }
        catch {
            # A refused guard stops the script before its last line: remove the temporary guard procedure, keep the data.
            Invoke-Sql 'DROP PROCEDURE IF EXISTS BE007_001_RollbackEmptyOnly;' | Out-Null
            throw "Down refused, nothing dropped: $($_.Exception.Message)"
        }
        if ((Get-Tables) -ne '0') { throw 'Down did not remove both tables; inspect before any retry.' }
        Write-Output "Rolled back migration 001 on $target (empty tables dropped)."
    }
}
