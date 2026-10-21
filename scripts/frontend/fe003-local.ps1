#requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Initialize','Start','Verify','RunApp','Stop')][string]$Action)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$instance = Join-Path $repo '.local/fe003-mysql'
$private = Join-Path $instance 'private'
$data = Join-Path $instance 'data'
$client = 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe'
$server = 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe'
$admin = Join-Path $private 'admin.cnf'
if (-not $IsWindows) { throw 'Windows native MySQL fixture only.' }
function Query([string]$Sql) {
    $answer = & $client "--defaults-file=$admin" --connect-timeout=3 --batch --raw --skip-column-names "--execute=$Sql" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Fixture query failed. Private database errors are not printed.' }
    return ($answer -join "`n").Trim()
}
function OwnInstance {
    $actual = (Query 'SELECT @@datadir').Replace('\','/').TrimEnd('/')
    if ($actual -ine $data.Replace('\','/').TrimEnd('/')) { throw 'Different MySQL instance. Refusing action.' }
}
function StartServer([bool]$Bootstrap) {
    if (Get-NetTCPConnection -State Listen -LocalPort 3338 -ErrorAction SilentlyContinue) { throw 'Port3338 occupied; nothing stopped or overwritten.' }
    $arguments = @('--no-defaults', '--basedir="C:/Program Files/MySQL/MySQL Server 8.0"',
        "--datadir=`"$($data.Replace('\','/'))`"", '--port=3338', '--bind-address=127.0.0.1', '--mysqlx=OFF',
        '--skip-log-bin', '--skip-name-resolve', '--secure-file-priv=NULL',
        "--log-error=`"$((Join-Path $instance 'server.log').Replace('\','/'))`"", "--pid-file=`"$((Join-Path $instance 'server.pid').Replace('\','/'))`"")
    if ($Bootstrap) { $arguments += "--init-file=`"$((Join-Path $private 'bootstrap.sql').Replace('\','/'))`"" }
    $process = Start-Process $server -ArgumentList $arguments -WindowStyle Hidden -PassThru
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        $process.Refresh(); if ($process.HasExited) { throw 'Fixture startup failed; inspect private local log.' }
        try { OwnInstance; return } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'Fixture readiness timed out. No reset attempted.'
}
function Connection([string]$File, [string]$Database) {
    $fields = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $private $File)) {
        if ($line.Contains('=')) { $parts=$line.Split('=',2); $fields[$parts[0]]=$parts[1] }
    }
    if ($fields.host -ne '127.0.0.1' -or $fields.port -ne '3338') { throw 'Unexpected fixture connection.' }
    $value = [System.Data.Common.DbConnectionStringBuilder]::new()
    foreach ($key in @{Server='127.0.0.1';Port=3338;Database=$Database;'User ID'=$fields.user;
        SslMode='Required';'Connection Timeout'=3;MaximumPoolSize=8}.GetEnumerator()) { $value[$key.Key]=$key.Value }
    $value['Password']=$fields.password
    return $value.ConnectionString
}
switch ($Action) {
    Initialize {
        if (Test-Path -LiteralPath $instance) { throw 'Fixture already exists. No reset or overwrite allowed.' }
        if (Get-NetTCPConnection -State Listen -LocalPort 3338 -ErrorAction SilentlyContinue) { throw 'Port3338 occupied.' }
        New-Item -ItemType Directory -Path $instance | Out-Null
        $acl=Get-Acl -LiteralPath $instance; $acl.SetAccessRuleProtection($true,$false)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        Set-Acl -LiteralPath $instance -AclObject $acl
        New-Item -ItemType Directory -Path $private,$data | Out-Null
        & dotnet run --project (Join-Path $repo 'tools/Sec001Fixtures/Sec001Fixtures.csproj') -c Release -- $private (Join-Path $repo 'database/sec001/schema.sql') 3338 be005-attempt-writer be007-history
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed.' }
        # Relocate only history DDL/grants into a separate schema. No company schema is used or modified.
        $bootstrap=Join-Path $private 'bootstrap.sql'
        $relocated = foreach ($line in [IO.File]::ReadAllLines($bootstrap)) {
            if ($line -eq 'USE bc_localai_sec001;') {
                'CREATE DATABASE bc_localai_history CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;'
                "GRANT ALL PRIVILEGES ON bc_localai_history.* TO 'bc_sec001_admin'@'127.0.0.1';"
                'USE bc_localai_history;'
            } else { $line.Replace('bc_localai_sec001.Conversations','bc_localai_history.Conversations').Replace('bc_localai_sec001.Messages','bc_localai_history.Messages') }
        }
        [IO.File]::WriteAllLines($bootstrap,$relocated,[Text.UTF8Encoding]::new($false))
        & $server --no-defaults --initialize '--basedir=C:/Program Files/MySQL/MySQL Server 8.0' "--datadir=$data" "--log-error=$(Join-Path $instance 'initialize.log')"
        if ($LASTEXITCODE -ne 0) { throw 'Fixture initialization failed. No existing database changed.' }
        try { StartServer $true } finally { Remove-Item -LiteralPath $bootstrap -Force }
        # A second synthetic active account enables cross-user browser verification. Passwords remain in the ACL-protected file.
        Query "UPDATE bc_localai_sec001.users SET lockout_end_utc=NULL, access_failed_count=0 WHERE is_active=1" | Out-Null
        Write-Output 'Created isolated FE-003 fixture at loopback3338; separate auth/history schemas. Bootstrap secret file removed.'
    }
    Start { StartServer $false; OwnInstance; Write-Output 'FE-003 fixture started.' }
    Verify {
        OwnInstance
        if ((Query "SELECT COUNT(*) FROM bc_localai_sec001.users") -ne '3' -or
            (Query "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='bc_localai_history' AND table_name='Conversations' AND column_name='DeletedAt'") -ne '1') { throw 'Fixture schema mismatch.' }
        Write-Output 'Verified own data directory, 3 synthetic users, separate history schema with current main retention column.'
    }
    RunApp {
        OwnInstance
        if (Get-NetTCPConnection -State Listen -LocalPort 5937 -ErrorAction SilentlyContinue) { throw 'Port5937 occupied. No process stopped.' }
        $env:Authentication__Enabled='true'; $env:Authentication__AllowLocalDevelopmentDatabase='true'
        $env:Authentication__ReaderConnectionString=Connection 'reader.cnf' 'bc_localai_sec001'
        $env:Authentication__WriterConnectionString=Connection 'writer.cnf' 'bc_localai_sec001'
        $env:Authentication__KeyDirectory=Join-Path $private 'keys'
        New-Item -ItemType Directory -Path $env:Authentication__KeyDirectory -Force | Out-Null
        $env:ChatHistory__Enabled='true'; $env:ChatHistory__ConnectionString=Connection 'history.cnf' 'bc_localai_history'
        $env:ASPNETCORE_ENVIRONMENT='Development'; $env:ASPNETCORE_URLS='https://localhost:5937'
        try { & dotnet run --project (Join-Path $repo 'BC-LocalAI/BC-LocalAI.csproj') -c Release --no-build --no-launch-profile }
        finally {
            foreach ($name in @('Authentication__Enabled','Authentication__AllowLocalDevelopmentDatabase','Authentication__ReaderConnectionString',
                'Authentication__WriterConnectionString','Authentication__KeyDirectory','ChatHistory__Enabled','ChatHistory__ConnectionString','ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS')) {
                [Environment]::SetEnvironmentVariable($name,$null,'Process')
            }
        }
    }
    Stop { OwnInstance; & $client "--defaults-file=$admin" --execute='SHUTDOWN'; if ($LASTEXITCODE -ne 0) { throw 'Fixture shutdown failed.' } }
}
