using MySqlConnector;

namespace BcAdmin;

internal enum AdminAction { Create, Disable, Enable, ResetPassword, SetRole, RevokeSessions }

internal sealed record NewAccount(string Username, string DisplayName, string Role, string PasswordHash);

/// <summary>Who ran the command. Actor is typed by the operator; OS user comes from Windows; DB user from MySQL CURRENT_USER().</summary>
internal sealed record AuditContext(string Actor, string OsUser);

/// <summary>Result of a change. Never carries the hash or stamp that was written.</summary>
internal sealed record AdminResult(bool Found, string? UserId, long AuditId);

internal interface IAdminAccountStore
{
    Task<string?> ResolveUserIdAsync(string idOrUsername, CancellationToken cancellationToken);
    Task<AdminResult> CreateAsync(NewAccount account, AuditContext audit, CancellationToken cancellationToken);
    /// <summary>Applies one change to an existing account, rotates its security stamp and writes one audit record, atomically.</summary>
    Task<AdminResult> ChangeAsync(string userId, AdminAction action, string? value, AuditContext audit, CancellationToken cancellationToken);
}

internal sealed class MySqlAdminAccountStore(string connectionString) : IAdminAccountStore
{
    public async Task<string?> ResolveUserIdAsync(string idOrUsername, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var isId = Guid.TryParseExact(idOrUsername, "D", out var id);
        command.CommandText = isId
            ? "SELECT id FROM users WHERE id = @value LIMIT 1"
            : "SELECT id FROM users WHERE normalized_username = @value LIMIT 1";
        command.Parameters.Add("@value", MySqlDbType.VarChar, 128).Value = isId ? id.ToString("D") : AccountPolicy.Normalize(idOrUsername);
        return await command.ExecuteScalarAsync(cancellationToken) is { } value ? Convert.ToString(value) : null;
    }

    public async Task<AdminResult> CreateAsync(NewAccount account, AuditContext audit, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
        var auditId = await AuditAsync(connection, transaction, AdminAction.Create, id, $"role={account.Role}", audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminResult(true, id, auditId);
    }

    public async Task<AdminResult> ChangeAsync(string userId, AdminAction action, string? value, AuditContext audit, CancellationToken cancellationToken)
    {
        // Fixed SQL per action; values are always parameters. Every change also rotates the security stamp.
        var (set, detail) = action switch
        {
            AdminAction.Disable => ("is_active = 0", null),
            AdminAction.Enable => ("is_active = 1", null),
            AdminAction.ResetPassword => ("password_hash = @value", null),
            AdminAction.SetRole => ("role = @value", $"role={value}"),
            AdminAction.RevokeSessions => ("", null),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (action is AdminAction.ResetPassword or AdminAction.SetRole && value is null) throw new ArgumentNullException(nameof(value));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
                return new AdminResult(false, null, 0); // nothing changed, so nothing to audit
            }
        }
        var auditId = await AuditAsync(connection, transaction, action, userId, detail, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminResult(true, userId, auditId);
    }

    /// <summary>In the same transaction as the change: if the audit record cannot be written, the change is rolled back.</summary>
    private static async Task<long> AuditAsync(MySqlConnection connection, MySqlTransaction transaction, AdminAction action, string userId,
        string? detail, AuditContext audit, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
            VALUES (@actor, @os, CURRENT_USER(), @action, @target, @detail);
            SELECT LAST_INSERT_ID();
            """, connection, transaction);
        command.Parameters.Add("@actor", MySqlDbType.VarChar, 128).Value = audit.Actor;
        command.Parameters.Add("@os", MySqlDbType.VarChar, 256).Value = audit.OsUser;
        command.Parameters.Add("@action", MySqlDbType.VarChar, 32).Value = ActionName(action);
        command.Parameters.Add("@target", MySqlDbType.VarChar, 36).Value = userId;
        command.Parameters.Add("@detail", MySqlDbType.VarChar, 64).Value = (object?)detail ?? DBNull.Value;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
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
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
