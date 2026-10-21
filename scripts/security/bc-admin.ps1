#requires -Version 7
# SEC-003 admin account script. Thin wrapper: every rule lives in tools/BcAdmin (see docs/security/SEC-003-ADMIN-GUIDE.md).
# Example: pwsh -File scripts/security/bc-admin.ps1 disable --db-config C:\BcAdmin\admin-writer.cnf --actor "Somchai" --user alice
# Never pass a password as an argument; the tool asks for it at a hidden prompt.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../tools/BcAdmin/BcAdmin.csproj'
# No other dotnet-run options: dotnet run forwards options it does not know to the tool, which refuses them.
& dotnet run --project $project -c Release -- @args
exit $LASTEXITCODE
