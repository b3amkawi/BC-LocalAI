#Requires -Version 7
# BE-007 chat history retention purge (approved 2026-10-21). Operator tool: never run by the application.
#   Rule 1: a conversation the user deleted (soft delete) is erased with its messages after -DeletedRetentionDays (30).
#   Rule 2: all history of a closed account (users.is_active = 0) is erased once the account row has not changed for
#           -ClosedAccountRetentionDays (90). A closed account cannot sign in, so its row only changes through an
#           administrator; a later change only postpones the purge, it never brings it forward. A conversation whose
#           owner no longer exists in users is treated as closed once the conversation itself is that old.
#   Preview counts only; Purge deletes in ONE transaction and appends one JSON line per run to -AuditLog
#   (time, operator, target, rules, counts; never user ids, titles or message text).
# -DefaultsFile is a MySQL option file of the PURGE account (SELECT/DELETE on the history tables, SELECT on
# users.id/is_active/updated_at_utc). -ApprovedTarget must equal host:port/database, as in migrate.ps1.
param(
    [Parameter(Mandatory)][ValidateSet('Preview', 'Purge')][string]$Action,
    [Parameter(Mandatory)][string]$DefaultsFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]{1,64}$')][string]$Database,
    [Parameter(Mandatory)][string]$ApprovedTarget,
    [string]$AuditLog,
    [ValidateRange(1, 3650)][int]$DeletedRetentionDays = 30,
    [ValidateRange(1, 3650)][int]$ClosedAccountRetentionDays = 90,
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin'
)
$ErrorActionPreference = 'Stop'
$client = Join-Path $MySqlBin 'mysql.exe'
if (-not (Test-Path -LiteralPath $DefaultsFile)) { throw 'Defaults file not found.' }
if ($Action -eq 'Purge' -and -not $AuditLog) { throw 'Purge needs -AuditLog. Nothing was changed.' }
# The audit line is written after the commit: make sure it can be written before anything is deleted.
if ($Action -eq 'Purge' -and -not (Test-Path -LiteralPath (Split-Path -Parent ([IO.Path]::GetFullPath($AuditLog))) -PathType Container)) {
    throw 'The folder of -AuditLog does not exist. Nothing was changed.'
}

$fields = @{}
foreach ($line in Get-Content -LiteralPath $DefaultsFile) {
    if ($line -match '^\s*(host|port)\s*=\s*(.+?)\s*$') { $fields[$Matches[1]] = $Matches[2] }
}
$target = "$($fields.host):$($fields.port)/$Database"
if ($target -ne $ApprovedTarget) { throw "Target $target is not the approved target. Nothing was changed." }

function Invoke-Sql([string]$Sql) {
    # Through stdin, so the whole run is one session (one transaction for Purge).
    $output = $Sql | & $client "--defaults-file=$DefaultsFile" "--database=$Database" --batch --skip-column-names --ssl-mode=REQUIRED 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MySQL client failed: $(($output | Out-String).Trim())" }
    return ($output | Out-String).Trim()
}

# Integers only (validated above); the cut-offs are computed by the server clock in UTC.
$setup = @"
SET @deleted_cutoff = UTC_TIMESTAMP(6) - INTERVAL $DeletedRetentionDays DAY;
SET @closed_cutoff = UTC_TIMESTAMP(6) - INTERVAL $ClosedAccountRetentionDays DAY;
"@
$rule1 = 'c.DeletedAt IS NOT NULL AND c.DeletedAt <= @deleted_cutoff'
$rule2 = '(u.id IS NULL AND c.UpdatedAt <= @closed_cutoff) OR (u.is_active = 0 AND u.updated_at_utc <= @closed_cutoff)'
$closedJoin = 'LEFT JOIN users u ON u.id = c.UserId'

if ($Action -eq 'Preview') {
    $counts = Invoke-Sql @"
$setup
SELECT
  (SELECT COUNT(*) FROM Conversations c WHERE $rule1),
  (SELECT COUNT(*) FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId WHERE $rule1),
  (SELECT COUNT(*) FROM Conversations c $closedJoin WHERE NOT ($rule1) AND ($rule2)),
  (SELECT COUNT(*) FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId $closedJoin WHERE NOT ($rule1) AND ($rule2));
"@
    $c = $counts -split "`t"
    Write-Output "Preview on $target : deleted > $DeletedRetentionDays days: $($c[0]) conversations / $($c[1]) messages; closed accounts > $ClosedAccountRetentionDays days: $($c[2]) conversations / $($c[3]) messages. Nothing was changed."
    return
}

$started = (Get-Date).ToUniversalTime().ToString('o')
$result = Invoke-Sql @"
$setup
SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED;
START TRANSACTION;
DELETE m FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId WHERE $rule1;
SET @rule1_messages = ROW_COUNT();
DELETE c FROM Conversations c WHERE $rule1;
SET @rule1_conversations = ROW_COUNT();
DELETE m FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId $closedJoin WHERE $rule2;
SET @rule2_messages = ROW_COUNT();
DELETE c FROM Conversations c $closedJoin WHERE $rule2;
SET @rule2_conversations = ROW_COUNT();
COMMIT;
SELECT @rule1_conversations, @rule1_messages, @rule2_conversations, @rule2_messages;
"@
$c = $result -split "`t"
$audit = [ordered]@{
    event = 'chat_history_purge'; started_utc = $started; finished_utc = (Get-Date).ToUniversalTime().ToString('o')
    operator = [Environment]::UserName; target = $target
    deleted_retention_days = $DeletedRetentionDays; closed_account_retention_days = $ClosedAccountRetentionDays
    deleted_conversations = [int]$c[0]; deleted_messages = [int]$c[1]
    closed_account_conversations = [int]$c[2]; closed_account_messages = [int]$c[3]
}
Add-Content -LiteralPath $AuditLog -Value ($audit | ConvertTo-Json -Compress) -Encoding utf8
Write-Output "Purged on $target : deleted > $DeletedRetentionDays days: $($c[0]) conversations / $($c[1]) messages; closed accounts > $ClosedAccountRetentionDays days: $($c[2]) conversations / $($c[3]) messages. Audit: one line appended."
