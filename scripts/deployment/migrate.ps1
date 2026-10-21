#Requires -Version 7
<#
.SYNOPSIS
Applies the reviewed BC-LocalAI schema to one explicitly approved MySQL database.

.DESCRIPTION
This is an operator command, never application-startup code. It records a SHA-256 for every applied migration and refuses
schema drift, a partially pre-existing target, or an approved-target mismatch. MySQL DDL commits implicitly: on failure,
inspect Status and the target before retrying. The script never creates a database, MySQL account, grant, or application user.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Status', 'Up')][string]$Action,
    [Parameter(Mandatory)][string]$DefaultsFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]{1,64}$')][string]$Database,
    [Parameter(Mandatory)][string]$ApprovedTarget,
    [switch]$AllowLocalDevelopmentDatabase,
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$client = Join-Path $MySqlBin 'mysql.exe'
if (-not (Test-Path -LiteralPath $DefaultsFile -PathType Leaf)) { throw 'Defaults file not found.' }
if (-not (Test-Path -LiteralPath $client -PathType Leaf)) { throw 'MySQL client not found.' }

$fields = @{}
foreach ($line in Get-Content -LiteralPath $DefaultsFile) {
    if ($line -match '^\s*(host|port|database)\s*=\s*(.+?)\s*$') { $fields[$Matches[1].ToLowerInvariant()] = $Matches[2] }
}
if (-not $fields.host -or -not $fields.port) { throw 'Defaults file must contain host and port.' }
if ($fields.database -and $fields.database -cne $Database) { throw 'Defaults-file database does not match -Database.' }
$target = "$($fields.host):$($fields.port)/$Database"
if ($target -cne $ApprovedTarget) { throw "Target $target is not the approved target. Nothing was changed." }
$loopback = $fields.host -in @('127.0.0.1', '::1')
if ($AllowLocalDevelopmentDatabase -and -not $loopback) { throw '-AllowLocalDevelopmentDatabase is restricted to a loopback host.' }
$sslMode = if ($AllowLocalDevelopmentDatabase) { 'REQUIRED' } else { 'VERIFY_IDENTITY' }

$migrations = @(
    [pscustomobject]@{
        Version = '001-accounts'
        Path = Join-Path $repo 'database/deployment/001-accounts.up.sql'
        ExistingObjects = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='users';"
    },
    [pscustomobject]@{
        Version = '002-chat-history'
        Path = Join-Path $repo 'database/chat-history/001-initial.up.sql'
        ExistingObjects = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('Conversations','Messages');"
    },
    [pscustomobject]@{
        Version = '003-admin-audit'
        Path = Join-Path $repo 'database/sec003/admin-audit.sql'
        ExistingObjects = "SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='admin_audit_log') + (SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema=DATABASE() AND trigger_name IN ('trg_users_admin_audit_insert','trg_users_admin_audit_update'));"
    }
)
foreach ($migration in $migrations) {
    if (-not (Test-Path -LiteralPath $migration.Path -PathType Leaf)) { throw "Migration file is missing: $($migration.Version)." }
    $migration | Add-Member -NotePropertyName Sha256 -NotePropertyValue ((Get-FileHash -Algorithm SHA256 -LiteralPath $migration.Path).Hash.ToLowerInvariant())
}

function Invoke-Sql([string]$Sql) {
    $output = $Sql | & $client "--defaults-file=$DefaultsFile" "--database=$Database" --batch --raw --skip-column-names "--ssl-mode=$sslMode" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'MySQL command failed. Inspect the database server log; credentials and raw client output were not printed.' }
    return ($output | Out-String).Trim()
}

function Initialize-Journal {
    Invoke-Sql @'
CREATE TABLE IF NOT EXISTS bc_schema_migrations (
    version VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    applied_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    CONSTRAINT ck_bc_schema_migrations_sha CHECK (sha256 REGEXP '^[0-9a-f]{64}$')
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin;
'@ | Out-Null
}

function Get-Applied {
    $rows = Invoke-Sql 'SELECT CONCAT(version, CHAR(9), sha256) FROM bc_schema_migrations ORDER BY version;'
    $result = @{}
    foreach ($row in $rows -split "`r?`n") {
        if (-not $row) { continue }
        $parts = $row -split "`t", 2
        if ($parts.Count -ne 2) { throw 'Migration journal contains an invalid row.' }
        $result[$parts[0]] = $parts[1]
    }
    return $result
}

function Test-Journal {
    (Invoke-Sql "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='bc_schema_migrations';") -eq '1'
}

function Assert-JournalShape {
    $shape = Invoke-Sql @'
SELECT CONCAT(
    (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='bc_schema_migrations'), CHAR(9),
    (SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='bc_schema_migrations' AND index_name='PRIMARY' AND column_name='version'), CHAR(9),
    (SELECT engine FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='bc_schema_migrations'));
'@
    if ($shape -cne "3`t1`tInnoDB") { throw 'Migration journal schema is invalid. Refusing to continue.' }
}

$journalExists = Test-Journal
if ($Action -eq 'Status' -and -not $journalExists) {
    $managedObjects = 0
    foreach ($migration in $migrations) { $managedObjects += [int](Invoke-Sql $migration.ExistingObjects) }
    if ($managedObjects -ne 0) { throw 'Managed schema objects exist without a migration journal. Reconcile manually.' }
    foreach ($migration in $migrations) { Write-Output "$($migration.Version): pending sha256=$($migration.Sha256)" }
    Write-Output "Target ${target}: 0 of $($migrations.Count) migrations applied."
    return
}
if (-not $journalExists) { Initialize-Journal }
Assert-JournalShape
$applied = Get-Applied
$knownVersions = @($migrations.Version)
foreach ($version in $applied.Keys) {
    if ($version -notin $knownVersions) { throw "Database contains unknown migration $version. Refusing to continue." }
}
foreach ($migration in $migrations) {
    if ($applied.ContainsKey($migration.Version) -and $applied[$migration.Version] -cne $migration.Sha256) {
        throw "Checksum mismatch for applied migration $($migration.Version). Refusing schema drift."
    }
}
$pendingSeen = $false
foreach ($migration in $migrations) {
    if (-not $applied.ContainsKey($migration.Version)) { $pendingSeen = $true; continue }
    if ($pendingSeen) { throw "Migration journal is not a contiguous prefix at $($migration.Version). Refusing to continue." }
}

if ($Action -eq 'Status') {
    foreach ($migration in $migrations) {
        $state = if ($applied.ContainsKey($migration.Version)) { 'applied' } else { 'pending' }
        Write-Output "$($migration.Version): $state sha256=$($migration.Sha256)"
    }
    Write-Output "Target ${target}: $($applied.Count) of $($migrations.Count) migrations applied."
    return
}

foreach ($migration in $migrations) {
    if ($applied.ContainsKey($migration.Version)) { continue }
    $existing = Invoke-Sql $migration.ExistingObjects
    if ($existing -ne '0') {
        throw "Migration $($migration.Version) found $existing pre-existing target object(s) without a journal record. Reconcile manually; nothing was applied by this step."
    }

    $source = $migration.Path.Replace('\', '/')
    $version = $migration.Version
    $sha256 = $migration.Sha256
    Invoke-Sql @"
SET @bc_migration_lock = GET_LOCK(CONCAT(DATABASE(), ':bc-localai-schema'), 10);
DROP PROCEDURE IF EXISTS bc_assert_migration_lock;
DELIMITER `$`$
CREATE PROCEDURE bc_assert_migration_lock()
BEGIN
    IF COALESCE(@bc_migration_lock, 0) <> 1 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Could not acquire BC-LocalAI migration lock';
    END IF;
END`$`$
DELIMITER ;
CALL bc_assert_migration_lock();
DROP PROCEDURE bc_assert_migration_lock;
source $source
INSERT INTO bc_schema_migrations(version, sha256) VALUES ('$version', '$sha256');
DO RELEASE_LOCK(CONCAT(DATABASE(), ':bc-localai-schema'));
"@ | Out-Null

    $applied = Get-Applied
    if (-not $applied.ContainsKey($version) -or $applied[$version] -cne $sha256) {
        throw "Migration $version did not produce the expected journal record. Inspect the target before retrying."
    }
    Write-Output "Applied $version to $target."
}

Write-Output "Target $target is current: $($migrations.Count) of $($migrations.Count) migrations applied."
