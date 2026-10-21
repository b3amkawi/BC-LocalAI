namespace BcAdmin;

/// <summary>
/// The only MySQL privileges the admin writer account gets (SEC-003). Shared by the DBA SQL (`print-grants`) and the local fixture,
/// so the documented grants and the tested grants cannot drift apart.
/// Unqualified table names: run in the BC-LocalAI database (mysql --database=...), never ON *.*.
/// </summary>
internal static class AdminWriterGrants
{
    internal const string DefaultUser = "bc_admin_writer";

    internal static string[] Create(string user, string host)
    {
        if (user.Length is 0 or > 32 || !user.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ||
            host.Length is 0 or > 255 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '%' or ':'))
            throw new ArgumentException("Only a plain account name and host may be used.");
        var account = $"'{user}'@'{host}'";
        return
        [
            // SELECT is needed for the WHERE clauses and to resolve a username to its id. No password_hash, no security_stamp.
            $"GRANT SELECT (id, normalized_username, is_active, role) ON users TO {account};",
            $"GRANT INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO {account};",
            $"GRANT UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO {account};",
            // Append-only from the writer's side: it can neither read, change nor delete audit records.
            $"GRANT INSERT (actor, os_user, db_user, action, target_user_id, detail) ON admin_audit_log TO {account};"
        ];
    }
}
