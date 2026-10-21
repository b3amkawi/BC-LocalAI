using BC_LocalAI.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace BC_LocalAI.Services.Authentication;

public interface IAuthenticationAttemptStore
{
    // null id/stamp performs the same bounded DB roundtrip without mutating an account (unknown/ineligible user).
    Task<bool> RecordAsync(string? userId, string? expectedStamp, bool passwordVerified, CancellationToken cancellationToken);
}

public sealed class MySqlAuthenticationAttemptStore(IOptions<AuthenticationOptions> options) : IAuthenticationAttemptStore
{
    public async Task<bool> RecordAsync(string? userId, string? expectedStamp, bool passwordVerified, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(options.Value.WriterConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // InnoDB serializes this single-row UPDATE. MySQL single-table SET assignments are left-to-right:
            // calculate the count from the OLD lockout first, then derive lockout from the NEW count.
            // The conditional predicate is rechecked under the row lock: concurrent failures cannot bypass a lock,
            // and a stale successful verification cannot clear an active lock or an admin's changed security stamp.
            command.CommandText = """
                UPDATE users
                SET access_failed_count = CASE
                      WHEN @verified THEN 0
                      WHEN lockout_end_utc IS NOT NULL THEN 1
                      ELSE LEAST(access_failed_count + 1, @threshold) END,
                    lockout_end_utc = CASE
                      WHEN @verified THEN NULL
                      WHEN access_failed_count >= @threshold THEN TIMESTAMPADD(MINUTE, @minutes, UTC_TIMESTAMP(6))
                      ELSE NULL END,
                    updated_at_utc = UTC_TIMESTAMP(6)
                WHERE id = @id AND security_stamp = @stamp AND is_active = 1
                  AND (lockout_end_utc IS NULL OR lockout_end_utc <= UTC_TIMESTAMP(6))
                """;
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;
            command.Parameters.Add("@id", MySqlDbType.VarChar, 36).Value = (object?)userId ?? DBNull.Value;
            command.Parameters.Add("@stamp", MySqlDbType.VarChar, 64).Value = (object?)expectedStamp ?? DBNull.Value;
            command.Parameters.Add("@verified", MySqlDbType.Bool).Value = passwordVerified;
            command.Parameters.Add("@threshold", MySqlDbType.Int32).Value = options.Value.LockoutThreshold;
            command.Parameters.Add("@minutes", MySqlDbType.Int32).Value = options.Value.LockoutMinutes;
            // Found-row semantics are required and validated (UseAffectedRows=false). Never retry an ambiguous write.
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is MySqlException or TimeoutException or OperationCanceledException or InvalidOperationException or FormatException)
        { throw new AuthenticationDependencyException(); }
    }
}
