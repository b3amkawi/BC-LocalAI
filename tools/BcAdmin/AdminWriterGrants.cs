namespace BcAdmin;

/// <summary>
/// The only MySQL privileges the admin writer account gets (SEC-003). Shared by the DBA SQL (`print-grants`,
/// database/sec003/admin-writer-grants.sql) and the local fixture, so the documented grants and the tested grants cannot drift apart.
/// Unqualified table names: run in the BC-LocalAI database (mysql --database=...), never ON *.*.
/// No privilege on admin_audit_log: the database's triggers write the audit (database/sec003/admin-audit.sql).
/// </summary>
internal static class AdminWriterGrants
{
    internal const string DefaultUser = "bc_admin_writer";

    internal static string[] Create(string user, string host)
    {
        var account = Account(user, host);
        return
        [
            // SELECT only for the WHERE clauses (id, normalized_username) and to read a username for the password check.
            // No password_hash, no security_stamp, no is_active, no role.
            $"GRANT SELECT (id, normalized_username) ON users TO {account};",
            $"GRANT INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO {account};",
            $"GRANT UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO {account};"
        ];
    }

    /// <summary>
    /// Account settings applied after CREATE USER: TLS required, a brute-force lock on the account itself and a small
    /// connection limit (the script uses one connection per command). MySQL 8.0.19+.
    /// </summary>
    internal static string AccountSettings(string user, string host) =>
        $"ALTER USER {Account(user, host)} REQUIRE SSL WITH MAX_USER_CONNECTIONS 4 FAILED_LOGIN_ATTEMPTS 5 PASSWORD_LOCK_TIME 1;";

    private static string Account(string user, string host)
    {
        // A fixed host or a netmask (10.0.0.0/255.255.255.0) only: no '%' or '_' wildcard, so the account is tied to known machines.
        if (user.Length is 0 or > 32 || !user.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ||
            host.Length is 0 or > 255 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '/'))
            throw new ArgumentException("Only a plain account name and a fixed host or netmask may be used (no % wildcard).");
        return $"'{user}'@'{host}'";
    }
}
