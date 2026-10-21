using BC_LocalAI.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace BC_LocalAI.Services.Authentication;

// Internal persistence projection, never a public response or a logging payload.
public sealed class AuthenticationAccount
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public required string PasswordHash { get; init; }
    public required bool IsActive { get; init; }
    public required string Role { get; init; }
    public required string SecurityStamp { get; init; }
    public DateTimeOffset? LockoutEndUtc { get; init; }
    public override string ToString() => "AuthenticationAccount [REDACTED]";
}

public interface IAuthenticationRepository
{
    Task<AuthenticationAccount?> FindAsync(string normalizedUsername, CancellationToken cancellationToken);
}

public sealed class AuthenticationDependencyException : Exception
{
    public AuthenticationDependencyException() : base("Authentication dependency unavailable.") { }
}

public sealed class MySqlAuthenticationRepository(IOptions<AuthenticationOptions> options) : IAuthenticationRepository
{
    public async Task<AuthenticationAccount?> FindAsync(string normalizedUsername, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(options.Value.ReaderConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // Schema and identifiers are fixed from SEC-001, not input/configuration. No SELECT *, writes or retry.
            command.CommandText = "SELECT id, username, display_name, password_hash, is_active, role, lockout_end_utc, security_stamp FROM users WHERE normalized_username = @username LIMIT 1";
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;
            command.Parameters.Add("@username", MySqlDbType.VarChar, 128).Value = normalizedUsername;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new AuthenticationAccount
            {
                Id = reader.GetGuid(0).ToString("D"), Username = reader.GetString(1), DisplayName = reader.GetString(2),
                PasswordHash = reader.GetString(3), IsActive = reader.GetBoolean(4), Role = reader.GetString(5),
                LockoutEndUtc = reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                SecurityStamp = reader.GetString(7)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is MySqlException or TimeoutException or OperationCanceledException or InvalidOperationException or InvalidCastException or FormatException)
        {
            // Do not retain/serialize/log an inner exception containing schema, SQL or connection details.
            throw new AuthenticationDependencyException();
        }
    }
}
