#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Initialize', 'Start', 'Stop', 'Verify')][string]$Action,
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$instance = Join-Path $repo '.local/sec001-mysql-v2'
$private = Join-Path $instance 'private'
$data = Join-Path $instance 'data'
$ready = Join-Path $instance 'initialized'
$server = Join-Path $MySqlBin 'mysqld.exe'
$client = Join-Path $MySqlBin 'mysql.exe'
$admin = Join-Path $private 'admin.cnf'
$reader = Join-Path $private 'reader.cnf'
if (-not $IsWindows) { throw 'This script is for native MySQL on Windows only.' }
foreach ($binary in @($server, $client)) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing MySQL executable: $binary" }
}

function Invoke-Query([string]$Sql, [string]$Options = $admin) {
    $result = & $client "--defaults-file=$Options" --connect-timeout=3 --batch --raw --skip-column-names "--execute=$Sql" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'MySQL query failed. No SQL, credentials or raw error output disclosed.' }
    return ($result -join "`n").Trim()
}
function Confirm-OwnInstance {
    if (-not (Test-Path -LiteralPath $ready)) { throw 'SEC-001 instance has not completed initialization.' }
    $actual = (Invoke-Query 'SELECT @@datadir;').Replace('\', '/').TrimEnd('/')
    if ($actual -ine $data.Replace('\', '/').TrimEnd('/')) { throw 'Wrong instance: data directory does not match. Refusing action.' }
}
function Start-OwnServer([bool]$Bootstrap) {
    if (Get-NetTCPConnection -State Listen -LocalPort 3307 -ErrorAction SilentlyContinue) {
        throw 'Port 3307 is already occupied. No existing service has been changed.'
    }
    $arguments = @('--no-defaults', "--basedir=`"$((Split-Path $MySqlBin).Replace('\','/'))`"",
        "--datadir=`"$($data.Replace('\','/'))`"", '--port=3307', '--bind-address=127.0.0.1',
        '--mysqlx=OFF', '--skip-log-bin', '--skip-name-resolve', '--secure-file-priv=NULL',
        "--log-error=`"$((Join-Path $instance 'server.log').Replace('\','/'))`"",
        "--pid-file=`"$((Join-Path $instance 'server.pid').Replace('\','/'))`"")
    if ($Bootstrap) { $arguments += "--init-file=`"$((Join-Path $private 'bootstrap.sql').Replace('\','/'))`"" }
    $process = Start-Process -FilePath $server -ArgumentList $arguments -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'MySQL exited during startup. Inspect the private local server log; do not publish it.' }
        try {
            if ((Invoke-Query 'SELECT COUNT(*) FROM bc_localai_sec001.users;') -eq '3') { return }
        } catch { } # Bounded readiness check; startup failure is reported below, not treated as success.
        Start-Sleep -Milliseconds 500
    }
    throw 'MySQL did not become ready within the bounded startup window. No automatic reset performed.'
}

switch ($Action) {
    'Initialize' {
        if (Test-Path -LiteralPath $instance) { throw 'Instance directory already exists. Use Start/Verify; never reset automatically.' }
        if (Get-NetTCPConnection -State Listen -LocalPort 3307 -ErrorAction SilentlyContinue) { throw 'Port 3307 is occupied.' }
        New-Item -ItemType Directory -Path $instance -Force | Out-Null
        # Remove inherited access before writing any credentials; only the current Windows identity and SYSTEM.
        $acl = Get-Acl -LiteralPath $instance
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
                [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl',
                'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $instance -AclObject $acl
        New-Item -ItemType Directory -Path $private, $data | Out-Null
        & dotnet run --project (Join-Path $repo 'tools/Sec001Fixtures/Sec001Fixtures.csproj') -c Release -- $private (Join-Path $repo 'database/sec001/schema.sql')
        if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed. No database initialization attempted.' }
        & $server --no-defaults --initialize "--basedir=$(Split-Path $MySqlBin)" "--datadir=$data" "--log-error=$(Join-Path $instance 'initialize.log')"
        if ($LASTEXITCODE -ne 0) { throw 'MySQL initialization failed; inspect private local log. No existing database was changed.' }
        Start-OwnServer $true
        # Marker is local state, never a substitute for the data-directory identity check.
        [IO.File]::WriteAllText($ready, 'SEC-001 isolated development instance')
        Confirm-OwnInstance
        Write-Output 'Created isolated MySQL at 127.0.0.1:3307, database bc_localai_sec001, 3 synthetic accounts.'
    }
    'Start' {
        if (-not (Test-Path -LiteralPath $ready)) { throw 'Initialize successfully before Start. Partial initialization is never reset.' }
        Start-OwnServer $false
        Confirm-OwnInstance
        Write-Output 'SEC-001 instance started on loopback port 3307.'
    }
    'Stop' {
        Confirm-OwnInstance
        $serverPid = [int](Get-Content -LiteralPath (Join-Path $instance 'server.pid') -Raw)
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$serverPid"
        if ($process.ExecutablePath -ine $server -or -not $process.CommandLine.Contains($data.Replace('\','/'))) {
            throw 'Process identity does not match the isolated instance. Stop refused.'
        }
        & (Join-Path $MySqlBin 'mysqladmin.exe') "--defaults-file=$admin" shutdown 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Could not stop the isolated instance.' }
        # mysqladmin acknowledges the request before InnoDB has released its files.
        if (Get-Process -Id $serverPid -ErrorAction SilentlyContinue) {
            Wait-Process -Id $serverPid -Timeout 30 -ErrorAction Stop
        }
        Write-Output 'Stopped SEC-001 instance. Data retained; MySQL80 service untouched.'
    }
    'Verify' {
        Confirm-OwnInstance
        $checks = [ordered]@{
            users = (Invoke-Query 'SELECT COUNT(*) FROM bc_localai_sec001.users;') -eq '3'
            states = (Invoke-Query 'SELECT SUM(is_active=0), SUM(lockout_end_utc > UTC_TIMESTAMP()) FROM bc_localai_sec001.users;') -eq "1`t1"
            no_admin_fixture = (Invoke-Query "SELECT COUNT(*) FROM bc_localai_sec001.users WHERE role='Admin';") -eq '0'
            no_plaintext_column = (Invoke-Query "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='bc_localai_sec001' AND column_name IN ('password','plaintext_password');") -eq '0'
            reader_can_read = (Invoke-Query 'SELECT COUNT(*) FROM bc_localai_sec001.users;' $reader) -eq '3'
            unknown_user = (Invoke-Query "SELECT COUNT(*) FROM bc_localai_sec001.users WHERE normalized_username='DOES.NOT.EXIST';" $reader) -eq '0'
            loopback_binding = (Invoke-Query 'SELECT @@bind_address;') -eq '127.0.0.1'
            mysqlx_disabled = (Invoke-Query "SELECT COUNT(*) FROM information_schema.plugins WHERE plugin_name='mysqlx' AND plugin_status='ACTIVE';") -eq '0'
        }
        # A harmless no-op must still be denied, proving the reader has no UPDATE grant.
        $denied = & $client "--defaults-file=$reader" --batch --execute='UPDATE bc_localai_sec001.users SET is_active=is_active WHERE 1=0;' 2>&1
        $checks.reader_cannot_write = $LASTEXITCODE -ne 0 -and ($denied -join '') -match '1142'
        $duplicate = & $client "--defaults-file=$admin" --batch --execute='START TRANSACTION; INSERT INTO bc_localai_sec001.users (id,username,normalized_username,display_name,password_hash,security_stamp) SELECT UUID(),username,normalized_username,display_name,password_hash,security_stamp FROM bc_localai_sec001.users LIMIT 1; ROLLBACK;' 2>&1
        $checks.duplicate_rejected = $LASTEXITCODE -ne 0 -and ($duplicate -join '') -match '1062' -and ($duplicate -join '') -match 'ux_users_normalized_username'
        # Keep hashes in process memory/stdin, never in tool output, command-line arguments or evidence.
        $stored = @(foreach ($line in (Invoke-Query 'SELECT username,password_hash FROM bc_localai_sec001.users;' $reader) -split "`n") {
            $fields = $line -split "`t"
            @{ Username = $fields[0]; PasswordHash = $fields[1] }
        })
        $stored | ConvertTo-Json -AsArray -Compress | & dotnet run --no-build -c Release --project (Join-Path $repo 'tools/Sec001Fixtures/Sec001Fixtures.csproj') -- verify-db $private
        $checks.stored_password_hashes = $LASTEXITCODE -eq 0
        foreach ($check in $checks.GetEnumerator()) { Write-Output "$($check.Key): $($check.Value)" }
        if ($checks.Values -contains $false) { throw 'One or more local MySQL checks failed.' }
        Write-Output "Passed $($checks.Count)/$($checks.Count) local database checks. Login API/session tests are not implemented here."
    }
}
