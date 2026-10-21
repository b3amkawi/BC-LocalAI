using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

// Every test uses the same fixture tables: one at a time, so one test's clean-up never lands in another test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BE007.MySqlTests;

/// <summary>
/// Settings of the isolated BE-007 fixture (scripts/security/local-mysql.ps1 -InstanceName be007-mysql -Port 3320).
/// Refuses anything that is not the dedicated loopback instance, so these tests can never touch another database.
/// </summary>
internal static class Be007Fixture
{
    public sealed record Account(string Username, string Password, string State);

    public static string Directory => Environment.GetEnvironmentVariable("BE007_MYSQL_PRIVATE_DIRECTORY") ??
        throw new InvalidOperationException("Set BE007_MYSQL_PRIVATE_DIRECTORY to the be007-mysql fixture private directory.");

    private static Dictionary<string, string> Read(string file) => File.ReadAllLines(Path.Combine(Directory, file))
        .Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    public static string Connection(string file)
    {
        var fields = Read(file);
        if (fields["host"] != "127.0.0.1" || fields["port"] != "3320")
            throw new InvalidOperationException("Only the dedicated BE-007 loopback fixture at port 3320 is allowed.");
        return new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = 3320, UserID = fields["user"], Password = fields["password"],
            Database = "bc_localai_sec001", SslMode = MySqlSslMode.Required, ConnectionTimeout = 3, MaximumPoolSize = 8
        }.ConnectionString;
    }

    public static string History => Connection("history.cnf");
    public static string Admin => Connection("admin.cnf");
    public static string Reader => Connection("reader.cnf");
    public static string Writer => Connection("writer.cnf");

    public static Account Active => JsonSerializer.Deserialize<Account[]>(File.ReadAllText(Path.Combine(Directory, "test-accounts.json")))!
        .Single(a => a.State == "active");

    public static MySqlChatHistoryRepository Repository(Func<DateTimeOffset>? clock = null) => new(
        Options.Create(new ChatHistoryOptions { Enabled = true, ConnectionString = History }),
        clock is null ? TimeProvider.System : new FuncClock(clock));

    /// <summary>Empties the two history tables with the admin account (the runtime account has no DELETE).</summary>
    public static async Task ClearHistory()
    {
        await AdminExecute("DELETE FROM Messages");
        await AdminExecute("DELETE FROM Conversations");
    }

    public static async Task<object?> AdminScalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    public static async Task<int> AdminExecute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed class FuncClock(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }
}
