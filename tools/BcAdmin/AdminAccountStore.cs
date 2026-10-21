using MySqlConnector;

namespace BcAdmin;

internal enum AdminAction { Create, Disable, Enable, ResetPassword, SetRole, RevokeSessions }

internal sealed record NewAccount(string Username, string DisplayName, string Role, string PasswordHash);

/// <summary>Who ran the command. Actor is typed by the operator; OS user comes from Windows; the DB user is taken by MySQL (USER()).</summary>
internal sealed record AuditContext(string Actor, string OsUser);

/// <summary>An existing account: its id and normalized username (for the password check). Never the hash or stamp.</summary>
internal sealed record AccountRef(string Id, string NormalizedUsername);

/// <summary>Result of a change. Never carries the hash or stamp that was written.</summary>
internal sealed record AdminResult(bool Found, string? UserId, long AuditId);

/// <summary>The COMMIT was sent but its result is unknown (connection lost, timeout): the change may or may not be saved.</summary>
internal sealed class AdminOutcomeUnknownException() : Exception("The outcome of the commit is unknown.");

internal interface IAdminAccountStore
{
    Task<AccountRef?> ResolveAsync(string idOrUsername, CancellationToken cancellationToken);
    Task<AdminResult> CreateAsync(NewAccount account, AuditContext audit, CancellationToken cancellationToken);
    /// <summary>Applies one change to an existing account and rotates its security stamp; the database trigger writes the audit row in the same transaction.</summary>
    Task<AdminResult> ChangeAsync(string userId, AdminAction action, string? value, AuditContext audit, CancellationToken cancellationToken);
}

internal sealed class MySqlAdminAccountStore(string connectionString) : IAdminAccountStore
{
    // Strict for this session's own statements (the triggers keep the strict mode they were created with).
    private const string StrictMode = "STRICT_ALL_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION";

    public async Task<AccountRef?> ResolveAsync(string idOrUsername, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var isId = Guid.TryParseExact(idOrUsername, "D", out var id);
        command.CommandText = isId
            ? "SELECT id, normalized_username FROM users WHERE id = @value LIMIT 1"
            : "SELECT id, normalized_username FROM users WHERE normalized_username = @value LIMIT 1";
        command.Parameters.Add("@value", MySqlDbType.VarChar, 128).Value = isId ? id.ToString("D") : AccountPolicy.Normalize(idOrUsername);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        // CHAR(36) ids come back as Guid from MySqlConnector.
        return new AccountRef(reader.GetGuid(0).ToString("D"), reader.GetString(1));
    }

    public async Task<AdminResult> CreateAsync(NewAccount account, AuditContext audit, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetAuditContextAsync(connection, transaction, audit, cancellationToken);
        await using (var insert = new MySqlCommand("""
            INSERT INTO users (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc)
            VALUES (@id, @username, @normalized, @display, @hash, 1, @role, @stamp, UTC_TIMESTAMP(6))
            """, connection, transaction))
        {
            insert.Parameters.Add("@id", MySqlDbType.VarChar, 36).Value = id;
            insert.Parameters.Add("@username", MySqlDbType.VarChar, 128).Value = account.Username;
            insert.Parameters.Add("@normalized", MySqlDbType.VarChar, 128).Value = AccountPolicy.Normalize(account.Username);
            insert.Parameters.Add("@display", MySqlDbType.VarChar, 200).Value = account.DisplayName;
            insert.Parameters.Add("@hash", MySqlDbType.VarChar, 512).Value = account.PasswordHash;
            insert.Parameters.Add("@role", MySqlDbType.VarChar, 16).Value = account.Role;
            insert.Parameters.Add("@stamp", MySqlDbType.VarChar, 64).Value = AccountPolicy.NewSecurityStamp();
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var auditId = await AuditIdAsync(connection, transaction, cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return new AdminResult(true, id, auditId);
    }

    public async Task<AdminResult> ChangeAsync(string userId, AdminAction action, string? value, AuditContext audit, CancellationToken cancellationToken)
    {
        // Fixed SQL per action; values are always parameters. Every change also rotates the security stamp.
        var set = action switch
        {
            AdminAction.Disable => "is_active = 0",
            AdminAction.Enable => "is_active = 1",
            AdminAction.ResetPassword => "password_hash = @value",
            AdminAction.SetRole => "role = @value",
            AdminAction.RevokeSessions => "",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (action is AdminAction.ResetPassword or AdminAction.SetRole && value is null) throw new ArgumentNullException(nameof(value));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetAuditContextAsync(connection, transaction, audit, cancellationToken);
        await using (var update = new MySqlCommand(
            $"UPDATE users SET {(set.Length > 0 ? set + ", " : "")}security_stamp = @stamp, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id",
            connection, transaction))
        {
            update.Parameters.Add("@id", MySqlDbType.VarChar, 36).Value = userId;
            update.Parameters.Add("@stamp", MySqlDbType.VarChar, 64).Value = AccountPolicy.NewSecurityStamp();
            if (value is not null) update.Parameters.Add("@value", MySqlDbType.VarChar, 512).Value = value;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AdminResult(false, null, 0); // nothing changed, so nothing was audited
            }
        }
        var auditId = await AuditIdAsync(connection, transaction, cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return new AdminResult(true, userId, auditId);
    }

    /// <summary>Session variables read by the audit triggers. Set per connection (no pooling), never shared.</summary>
    private static async Task SetAuditContextAsync(MySqlConnection connection, MySqlTransaction transaction, AuditContext audit, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SET @bc_admin_actor = @actor, @bc_admin_os_user = @os, @bc_admin_audit_id = NULL", connection, transaction);
        command.Parameters.Add("@actor", MySqlDbType.VarChar, 128).Value = audit.Actor;
        command.Parameters.Add("@os", MySqlDbType.VarChar, 256).Value = audit.OsUser;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The trigger records the change; no audit row means the database is missing the SEC-003 triggers, so nothing is saved.</summary>
    private static async Task<long> AuditIdAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT @bc_admin_audit_id", connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is { } id and not DBNull) return Convert.ToInt64(id);
        await transaction.RollbackAsync(cancellationToken);
        throw new AdminConfigException("The database did not write an audit record (SEC-003 triggers missing). Nothing changed.");
    }

    private static Task CommitAsync(MySqlTransaction transaction, CancellationToken cancellationToken) =>
        CommitOrOutcomeUnknownAsync(() => transaction.CommitAsync(cancellationToken));

    /// <summary>Runs the COMMIT; any failure of it means the outcome is unknown, never "nothing changed".</summary>
    internal static async Task CommitOrOutcomeUnknownAsync(Func<Task> commit)
    {
        try { await commit(); }
        catch (Exception exception) when (exception is MySqlException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // The server may have committed before the connection failed: never report "nothing changed" here.
            throw new AdminOutcomeUnknownException();
        }
    }

    internal static string ActionName(AdminAction action) => action switch
    {
        AdminAction.Create => "create", AdminAction.Disable => "disable", AdminAction.Enable => "enable",
        AdminAction.ResetPassword => "reset_password", AdminAction.SetRole => "set_role", AdminAction.RevokeSessions => "revoke_sessions",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand($"SET SESSION sql_mode = '{StrictMode}'", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
