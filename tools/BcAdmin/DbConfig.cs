using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using MySqlConnector;

namespace BcAdmin;

internal sealed class AdminConfigException(string message) : Exception(message);

/// <summary>
/// Builds the admin writer's connection from a MySQL option file ([client] user/password/host/port/database, optional ssl-ca).
/// The file must be private to the operator; its values are never printed.
/// </summary>
internal static class DbConfig
{
    // Accounts that must never run account administration (SEC-001 / BE-005 least privilege).
    private static readonly string[] ReservedUsers = ["root", "bc_auth_reader", "bc_auth_attempt_writer", "bc_sec001_admin"];

    /// <param name="allowLocalDevelopmentDatabase">
    /// Like the app's Authentication:AllowLocalDevelopmentDatabase: only then may a loopback database skip certificate checks
    /// (TLS still required). Everything else needs ssl-ca and VerifyFull.
    /// </param>
    internal static string Load(string path, string? databaseOverride = null, bool allowLocalDevelopmentDatabase = false)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new AdminConfigException("Database config file not found (use a full path).");
        EnsurePrivate(path);
        var fields = Parse(File.ReadAllLines(path));
        string Field(string name) => fields.TryGetValue(name, out var value) && value.Length > 0
            ? value : throw new AdminConfigException($"Database config file has no '{name}'.");

        var user = Field("user");
        if (ReservedUsers.Contains(user, StringComparer.OrdinalIgnoreCase))
            throw new AdminConfigException("This database account may not administer accounts. Use the separate admin writer account.");
        var host = Field("host");
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host, UserID = user, Password = Field("password"),
            Port = fields.TryGetValue("port", out var port) ? (uint.TryParse(port, out var p) && p is > 0 and < 65536 ? p : throw new AdminConfigException("Invalid port.")) : 3306,
            Database = databaseOverride ?? Field("database"),
            ConnectionTimeout = 5, DefaultCommandTimeout = 10, Pooling = false,
            // Found rows (not changed rows): an UPDATE that matches the account always reports 1.
            UseAffectedRows = false, AllowLoadLocalInfile = false, AllowPublicKeyRetrieval = false, PersistSecurityInfo = false,
            // The audit triggers read @bc_admin_actor / @bc_admin_os_user set by this session.
            AllowUserVariables = true
        };
        // Same rule as the app (AuthenticationOptionsValidator): TLS always; certificate and host checked unless an
        // explicitly allowed local development database on loopback.
        var loopback = IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        if (fields.TryGetValue("ssl-ca", out var ca) && ca.Length > 0)
        {
            builder.SslCa = ca;
            builder.SslMode = MySqlSslMode.VerifyFull;
        }
        else if (loopback && allowLocalDevelopmentDatabase) builder.SslMode = MySqlSslMode.Required;
        else if (loopback) throw new AdminConfigException("Add 'ssl-ca' to the config file, or --local-dev-database for a local development database only.");
        else throw new AdminConfigException("A remote database needs 'ssl-ca' in the config file (certificate verification).");
        return builder.ConnectionString;
    }

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var inClient = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[')) { inClient = line == "[client]"; continue; }
            var separator = line.IndexOf('=');
            if (!inClient || separator <= 0) continue;
            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return fields;
    }

    /// <summary>Refuses a credential file that anyone but the operator, SYSTEM or Administrators can read.</summary>
    internal static void EnsurePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var allowed = new[]
            {
                WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            };
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
                    throw new AdminConfigException("Database config file is readable by other accounts. Restrict it to your account (and SYSTEM/Administrators).");
            }
        }
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
        {
            throw new AdminConfigException("Database config file is readable by other accounts (chmod 600).");
        }
    }
}
