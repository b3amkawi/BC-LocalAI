using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

namespace BE005.MySqlTests;

// Explicit separate suite: fails (does not silently skip/pass) unless an isolated, authorized fixture directory is provided.
public sealed partial class MySqlAuthenticationTests
{
    private static (string Connection, string Writer, Account[] Accounts, string Private) Settings()
    {
        var directory = Environment.GetEnvironmentVariable("BE005_MYSQL_PRIVATE_DIRECTORY") ??
            throw new InvalidOperationException("Set BE005_MYSQL_PRIVATE_DIRECTORY to the isolated fixture private directory. Do not supply credentials on the command line.");
        var fields = File.ReadAllLines(Path.Combine(directory, "reader.cnf")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        if (fields["host"] != "127.0.0.1" || fields["port"] != "3319" || fields["user"] != "bc_auth_reader")
            throw new InvalidOperationException("This suite only permits the dedicated BE-005 fixture reader at loopback port3319.");
        var connection = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = 3319, UserID = fields["user"], Password = fields["password"],
            Database = "bc_localai_sec001", SslMode = MySqlSslMode.Required, ConnectionTimeout = 3, MaximumPoolSize = 4
        };
        var accounts = JsonSerializer.Deserialize<Account[]>(File.ReadAllText(Path.Combine(directory, "test-accounts.json")))!;
        var writerFields = File.ReadAllLines(Path.Combine(directory, "writer.cnf")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        if (writerFields["user"] != "bc_auth_attempt_writer" || writerFields["host"] != "127.0.0.1" || writerFields["port"] != "3319")
            throw new InvalidOperationException("Unexpected fixture writer identity/instance.");
        var writer = new MySqlConnectionStringBuilder(connection.ConnectionString) { UserID = writerFields["user"], Password = writerFields["password"] };
        return (connection.ConnectionString, writer.ConnectionString, accounts, directory);
    }

    [Fact]
    public async Task ReadOnlyGrantParameterizedLookupAndCallerCancellationAreEnforced()
    {
        var settings = Settings();
        await using var connection = new MySqlConnection(settings.Connection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@port";
        Assert.Equal(3319, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "UPDATE users SET is_active = is_active WHERE 1 = 0";
        var denied = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(1142, denied.Number);
        var repository = new MySqlAuthenticationRepository(Options.Create(new AuthenticationOptions { ReaderConnectionString = settings.Connection }));
        Assert.NotNull(await repository.FindAsync("SEC001.ACTIVE", default));
        Assert.Null(await repository.FindAsync("' OR 1=1 --", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindAsync("SEC001.ACTIVE", new CancellationToken(true)));
    }

    [Fact]
    public async Task ActualMySqlLoginMeLogoutAndAllNegativeStatesWorkWithoutUpdatingHashes()
    {
        var settings = Settings();
        var before = await Fingerprints(settings.Connection);
        await AdminCommand(settings, "UPDATE users SET access_failed_count=0, lockout_end_utc=NULL WHERE normalized_username='SEC001.ACTIVE'");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
                ["Authentication:ReaderConnectionString"] = settings.Connection, ["Authentication:WriterConnectionString"] = settings.Writer,
                ["Authentication:KeyDirectory"] = settings.Private
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var negativeBodies = new List<string>();
        foreach (var state in new[] { "active", "disabled", "locked", "wrong", "unknown" })
        {
            var account = settings.Accounts.Single(item => item.State == (state is "wrong" or "unknown" ? "active" : state));
            var token = await Token(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = state == "unknown" ? "sec001.unknown" : account.Username.ToUpperInvariant(), password = state == "wrong" ? account.Password + "wrong" : account.Password })
            };
            request.Headers.Add("X-CSRF-TOKEN", token);
            using var login = await client.SendAsync(request);
            if (state != "active")
            {
                Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
                Assert.DoesNotContain(login.Headers.TryGetValues("Set-Cookie", out var values) ? values : [], value => value.StartsWith("__Host-BC.LocalAI=") && !value.StartsWith("__Host-BC.LocalAI=;"));
                negativeBodies.Add(await login.Content.ReadAsStringAsync());
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var me = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            Assert.Equal(await login.Content.ReadAsStringAsync(), await me.Content.ReadAsStringAsync());
            using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            logout.Headers.Add("X-CSRF-TOKEN", await Token(client));
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
        Assert.Single(negativeBodies.Distinct());
        var after = await Fingerprints(settings.Connection);
        Assert.True(before.SequenceEqual(after), "Authentication must not change or duplicate the stored hashes.");
    }

    [Fact]
    public async Task UnreachableDatabaseMapsToSafeDependencyFailure()
    {
        var settings = Settings();
        var connection = new MySqlConnectionStringBuilder(settings.Connection) { Port = 1, Pooling = false };
        var repository = new MySqlAuthenticationRepository(Options.Create(new AuthenticationOptions { ReaderConnectionString = connection.ConnectionString }));
        var error = await Assert.ThrowsAsync<AuthenticationDependencyException>(() => repository.FindAsync("SEC001.ACTIVE", default));
        Assert.Null(error.InnerException);
        Assert.Equal("Authentication dependency unavailable.", error.Message);
    }

    [Fact]
    public async Task BlockedQueryHonoursCommandDeadlineAndMidQueryCancellation()
    {
        var settings = Settings();
        await using var account = await TestAccount.Create();
        var fields = File.ReadAllLines(Path.Combine(settings.Private, "admin.cnf")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        var connection = new MySqlConnectionStringBuilder(settings.Connection) { UserID = fields["user"], Password = fields["password"] };
        await using var admin = new MySqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using var command = admin.CreateCommand(); command.CommandText = "LOCK TABLES users WRITE";
        await command.ExecuteNonQueryAsync(); // Isolated synthetic instance only; no row changes.
        try
        {
            var repository = new MySqlAuthenticationRepository(Options.Create(new AuthenticationOptions { ReaderConnectionString = settings.Connection, CommandTimeoutSeconds = 1 }));
            await Assert.ThrowsAsync<AuthenticationDependencyException>(() => repository.FindAsync("SEC001.ACTIVE", default));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindAsync("SEC001.ACTIVE", cancellation.Token));
            var writer = new MySqlAuthenticationAttemptStore(Options.Create(new AuthenticationOptions { WriterConnectionString = settings.Writer, CommandTimeoutSeconds = 1 }));
            await Assert.ThrowsAsync<AuthenticationDependencyException>(() => writer.RecordAsync(account.Id, account.Stamp, false, default));
            using var writerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.RecordAsync(account.Id, account.Stamp, false, writerCancellation.Token));
        }
        finally { command.CommandText = "UNLOCK TABLES"; await command.ExecuteNonQueryAsync(); }
        Assert.Equal(0, (await account.State()).Count);
    }

    private static async Task<string> Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/csrf"); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<string[]> Fingerprints(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT password_hash FROM users ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(); var hashes = new List<string>();
        while (await reader.ReadAsync()) hashes.Add(reader.GetString(0));
        return hashes.ToArray(); // Memory only, never assertion/log values.
    }

    private sealed record Account(string Username, string Password, string State);
}
