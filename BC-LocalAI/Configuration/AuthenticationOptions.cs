using System.Net;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace BC_LocalAI.Configuration;

public sealed class AuthenticationOptions
{
    public bool Enabled { get; set; }
    // Separate secret-provider values. Never put these in checked-in appsettings or logs.
    public string ReaderConnectionString { get; set; } = "";
    public string KeyDirectory { get; set; } = "";
    public bool AllowLocalDevelopmentDatabase { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 5;
    public int AccountAttemptsPerMinute { get; set; } = 5;
    public int IpAttemptsPerMinute { get; set; } = 30;
    public int GlobalAttemptsPerMinute { get; set; } = 120;
    public int ConcurrentLogins { get; set; } = 2;
    public int MaxSessions { get; set; } = 1024;
    public override string ToString() => "AuthenticationOptions [REDACTED]";
}

public sealed class AuthenticationOptionsValidator(IHostEnvironment environment) : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        // Deliberately generic: parser errors may contain connection-string values.
        try
        {
            var connection = new MySqlConnectionStringBuilder(options.ReaderConnectionString);
            var local = options.AllowLocalDevelopmentDatabase && environment.IsDevelopment() &&
                IPAddress.TryParse(connection.Server, out var ip) && IPAddress.IsLoopback(ip);
            if (string.IsNullOrWhiteSpace(connection.UserID) || connection.UserID.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                connection.UserID.Equals("root", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(connection.Password) || string.IsNullOrWhiteSpace(connection.Database) ||
                connection.ConnectionTimeout is < 1 or > 15 || connection.PersistSecurityInfo || connection.AllowLoadLocalInfile ||
                connection.AllowPublicKeyRetrieval || connection.MaximumPoolSize is < 1 or > 32 ||
                (!local && connection.SslMode != MySqlSslMode.VerifyFull) ||
                (local && connection.SslMode is not (MySqlSslMode.Required or MySqlSslMode.VerifyFull)) ||
                (options.AllowLocalDevelopmentDatabase && !local)) return Invalid();
            if (options.CommandTimeoutSeconds is < 1 or > 15 || options.ConcurrentLogins is < 1 or > 4 ||
                options.AccountAttemptsPerMinute is < 1 or > 30 || options.IpAttemptsPerMinute is < 1 or > 120 ||
                options.GlobalAttemptsPerMinute is < 1 or > 600 || options.MaxSessions is < 1 or > 4096 ||
                !Path.IsPathFullyQualified(options.KeyDirectory) || !Directory.Exists(options.KeyDirectory)) return Invalid();
            var keyPath = Path.GetFullPath(options.KeyDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var webRoot = environment is IWebHostEnvironment web && !string.IsNullOrWhiteSpace(web.WebRootPath)
                ? web.WebRootPath : Path.Combine(environment.ContentRootPath, "wwwroot");
            var publicPath = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (keyPath.StartsWith(publicPath, StringComparison.OrdinalIgnoreCase)) return Invalid();
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Invalid();
        }
    }

    private static ValidateOptionsResult Invalid() => ValidateOptionsResult.Fail("Authentication configuration is invalid; inspect private settings without logging credentials.");
}
