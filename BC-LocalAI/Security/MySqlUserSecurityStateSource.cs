using BC_LocalAI.Services.Authentication;
using Microsoft.Extensions.Options;
using MySqlConnector;
using AuthenticationOptions = BC_LocalAI.Configuration.AuthenticationOptions;

namespace BC_LocalAI.Security;

/// <summary>
/// Reads an account's current state with BE-005's read-only MySQL account (SEC-001 schema), on every authenticated request.
/// Only the columns needed to decide whether a session may continue; never the password hash.
/// </summary>
public sealed class MySqlUserSecurityStateSource(IOptions<AuthenticationOptions> options) : IUserSecurityStateSource
{
    public async Task<UserSecurityState?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(userId, "D", out _))
        {
            return null; // ids are UUIDs; anything else cannot be an account
        }

        try
        {
            await using var connection = new MySqlConnection(options.Value.ReaderConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // Fixed SQL from the SEC-001 schema; the id is always a parameter.
            command.CommandText = "SELECT id, is_active, lockout_end_utc, role, security_stamp FROM users WHERE id = @id LIMIT 1";
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;
            command.Parameters.Add("@id", MySqlDbType.VarChar, 36).Value = userId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new UserSecurityState(
                reader.GetGuid(0).ToString("D"),
                reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                reader.GetString(3),
                reader.GetString(4));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is MySqlException or TimeoutException or OperationCanceledException
                                              or InvalidOperationException or InvalidCastException or FormatException)
        {
            // Same rule as BE-005: never pass on an exception that may contain connection details or SQL.
            throw new AuthenticationDependencyException();
        }
    }
}
