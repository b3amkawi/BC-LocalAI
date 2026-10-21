# Shared server database migration and LAN access

This runbook prepares a **new shared BC-LocalAI MySQL database**. It does not expose MySQL or Ollama to users' computers.
The supported topology is:

`browser -> HTTPS BC-LocalAI server -> MySQL over TLS` and `BC-LocalAI server -> Ollama on loopback`

Only the HTTPS application port is reachable from the approved LAN. Keep MySQL restricted to the application/operator hosts and
keep Ollama on `127.0.0.1:11434`.

## Safety and prerequisites

- MySQL 8.0.29 or newer; use a new, explicitly approved database and take/restore-test backups before changing an existing target.
- Run the migration with a dedicated operator account, never an application runtime account. The account that creates the audit
  triggers becomes their `DEFINER`; preserve it with the limited privileges documented in
  [SEC-003 admin guide](../security/SEC-003-ADMIN-GUIDE.md).
- Create the database explicitly with `utf8mb4` / `utf8mb4_0900_bin`. The migration intentionally cannot create or drop databases,
  MySQL accounts, grants, or application users.
- Store credentials outside the repository in an ACL-protected MySQL option file. Never put connection strings in `appsettings.json`.

Example option file (`C:\BC-LocalAI\private\migrator.cnf`; replace every placeholder):

```ini
[client]
user=<dedicated-migrator-or-audit-definer>
password=<random-secret>
host=<mysql-dns-name>
port=3306
database=bc_localai
ssl-ca=C:\BC-LocalAI\private\mysql-ca.pem
```

Restrict it on Windows, for example:

```powershell
icacls C:\BC-LocalAI\private\migrator.cnf /inheritance:r /grant:r "$env:USERNAME`:F" "SYSTEM:F"
```

## Apply and verify

Review the target and the three SQL files before approval. `-ApprovedTarget` must exactly equal `host:port/database` from the
option file and command line.

```powershell
$target = 'mysql.internal.example:3306/bc_localai'
pwsh -File scripts/deployment/migrate.ps1 -Action Status `
  -DefaultsFile C:\BC-LocalAI\private\migrator.cnf -Database bc_localai -ApprovedTarget $target
pwsh -File scripts/deployment/migrate.ps1 -Action Up `
  -DefaultsFile C:\BC-LocalAI\private\migrator.cnf -Database bc_localai -ApprovedTarget $target
pwsh -File scripts/deployment/migrate.ps1 -Action Status `
  -DefaultsFile C:\BC-LocalAI\private\migrator.cnf -Database bc_localai -ApprovedTarget $target
```

The ordered migrations are:

1. `001-accounts`: `users`
2. `002-chat-history`: `Conversations`, `Messages`
3. `003-admin-audit`: `admin_audit_log` and two `users` audit triggers

`bc_schema_migrations` records the SHA-256 of every applied file. A changed checksum, unknown version, or target object that exists
without a journal record is a hard failure requiring DBA reconciliation. MySQL DDL commits implicitly, so there is deliberately no
automatic rollback or destructive `Down` command.

For an isolated developer fixture only, `-AllowLocalDevelopmentDatabase` permits MySQL `ssl-mode=REQUIRED` when `host` is exactly
`127.0.0.1` or `::1`. It is rejected for every non-loopback target; shared environments always use hostname verification and a CA.

After migration, the DBA provisions separate least-privilege runtime accounts described in
[BE-005 integration](../authentication/BE-005-INTEGRATION.md), [BE-007 design](../chat-history/BE-007-DESIGN.md), and
[SEC-003 admin guide](../security/SEC-003-ADMIN-GUIDE.md). Create the first named employee account through `bc-admin`; do not share
the synthetic `sec001.*` test accounts.

## Make the web application reachable

Migration does not expose the website. Publish one application server, install a certificate whose SAN matches its DNS name and is
trusted by client machines, and terminate HTTPS in IIS/reverse proxy or Kestrel. Configure secrets through the approved server-side
secret provider, enable Authentication and ChatHistory, keep `Ollama:BaseUrl=http://127.0.0.1:11434`, and persist Data Protection keys
in a directory readable only by the application identity.

Permit only the chosen HTTPS port from the approved LAN in the host/network firewall. Do not open ports 3306 or 11434 to browser
clients. Users then browse to the server DNS URL, for example `https://bc-localai.internal.example/`, and sign in with their own
account.

Before handoff, verify from a second machine: trusted HTTPS with no warning, login/logout, one streamed answer, history after refresh,
authorization between two different accounts, and audit rows for account administration. Production publication, firewall changes,
certificate issuance, DNS and real employee accounts require their respective owner approvals and are not performed by this migration.
