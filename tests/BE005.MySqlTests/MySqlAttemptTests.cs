using System.Security.Cryptography;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BE005.MySqlTests;

public sealed partial class MySqlAuthenticationTests
{
    [Fact]
    public async Task HttpLockoutSurvivesNewApplicationHostAndExpiredLockAllowsLogin()
    {
        await using var account = await TestAccount.Create();
        var settings = Settings();
        WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
                ["Authentication:ReaderConnectionString"] = settings.Connection, ["Authentication:WriterConnectionString"] = settings.Writer,
                ["Authentication:KeyDirectory"] = settings.Private
            }));
        });
        async Task<HttpResponseMessage> Login(HttpClient client, string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            { Content = JsonContent.Create(new { username = account.Username, password }) };
            request.Headers.Add("X-CSRF-TOKEN", await Token(client));
            return await client.SendAsync(request);
        }
        var clientOptions = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false };
        string? genericBody = null;
        await using (var firstHost = Factory())
        {
            using var firstClient = firstHost.CreateClient(clientOptions);
            for (var i = 0; i < 5; i++)
            {
                using var failure = await Login(firstClient, account.Password + "wrong");
                Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
                genericBody ??= await failure.Content.ReadAsStringAsync();
                Assert.Equal(genericBody, await failure.Content.ReadAsStringAsync());
            }
        }
        await using var secondHost = Factory(); // Resets all process-local attempt/session state, not the DB counter/lock.
        using var secondClient = secondHost.CreateClient(clientOptions);
        using var locked = await Login(secondClient, account.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(genericBody, await locked.Content.ReadAsStringAsync());
        Assert.False(locked.Headers.Contains("Set-Cookie"));
        Assert.Equal(5, (await account.State()).Count);
        await AdminCommand(settings, "UPDATE users SET lockout_end_utc=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE id=@id", account.Id);
        using var unlocked = await Login(secondClient, account.Password);
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Equal(0, (await account.State()).Count);
        Assert.Null((await account.State()).End);
        Assert.True(await account.HashUnchanged());
    }

    [Fact]
    public async Task FifthFailureLocksForFifteenMinutesAndCorrectPasswordCannotClearIt()
    {
        await using var account = await TestAccount.Create();
        var store = Store();
        for (var i = 1; i <= 5; i++)
        {
            Assert.True(await store.RecordAsync(account.Id, account.Stamp, false, default));
            var state = await account.State(); Assert.Equal(i, state.Count);
            if (i < 5) Assert.Null(state.End);
        }
        var locked = await account.State();
        Assert.InRange(locked.End!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        Assert.False(await store.RecordAsync(account.Id, account.Stamp, true, default));
        Assert.False(await store.RecordAsync(account.Id, account.Stamp, false, default));
        Assert.Equal(locked, await account.State()); // Locked attempts must not prolong the lock or change counts.
        var backend = new MySqlAuthenticationBackend(new MySqlAuthenticationRepository(AuthOptions()), new IdentityPasswordVerifier(),
            TimeProvider.System, NullLogger<MySqlAuthenticationBackend>.Instance, store);
        var result = await backend.AuthenticateAsync(new LoginRequest { Username = account.Username, Password = account.Password });
        Assert.Equal(AuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.User); Assert.Equal(locked, await account.State());
    }

    [Fact]
    public async Task ExpiredLockStartsAtOneAndSuccessfulLoginClearsTheCounterWithoutChangingHash()
    {
        await using var account = await TestAccount.Create();
        var store = Store();
        for (var i = 0; i < 5; i++) await store.RecordAsync(account.Id, account.Stamp, false, default);
        await AdminCommand(Settings(), "UPDATE users SET lockout_end_utc=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE id=@id", account.Id);
        Assert.True(await store.RecordAsync(account.Id, account.Stamp, false, default));
        var expired = await account.State(); Assert.Equal(1, expired.Count); Assert.Null(expired.End);
        // A new store/connection has the same persisted state, not a process-local counter.
        Assert.True(await Store().RecordAsync(account.Id, account.Stamp, true, default));
        var success = await account.State(); Assert.Equal(0, success.Count); Assert.Null(success.End);
        Assert.True(await account.HashUnchanged());
    }

    [Fact]
    public async Task ConcurrentFailuresCannotLoseIncrementsOrBypassTheThreshold()
    {
        await using var account = await TestAccount.Create();
        var store = Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.RecordAsync(account.Id, account.Stamp, false, default)));
        Assert.Equal(5, results.Count(result => result));
        var locked = await account.State(); Assert.Equal(5, locked.Count); Assert.NotNull(locked.End);
        Assert.False(await store.RecordAsync(account.Id, account.Stamp, true, default));
        Assert.True(await account.HashUnchanged());
    }

    [Fact]
    public async Task StaleStampDisabledDeletedAndUnknownAccountsCannotBeMutatedOrAuthenticated()
    {
        await using var account = await TestAccount.Create();
        var store = Store();
        await store.RecordAsync(account.Id, account.Stamp, false, default);
        var changed = account.Stamp[0] == 'A' ? 'B' : 'A';
        var wrongStamp = changed + account.Stamp[1..];
        Assert.False(await store.RecordAsync(account.Id, wrongStamp, true, default));
        Assert.False(await store.RecordAsync(null, null, false, default));
        Assert.False(await store.RecordAsync("' OR 1=1 --", wrongStamp, true, default));
        Assert.Equal(1, (await account.State()).Count);
        await AdminCommand(Settings(), "UPDATE users SET security_stamp=@stamp WHERE id=@id", account.Id, wrongStamp);
        Assert.False(await store.RecordAsync(account.Id, account.Stamp, true, default));
        await AdminCommand(Settings(), "UPDATE users SET is_active=0 WHERE id=@id", account.Id);
        Assert.False(await store.RecordAsync(account.Id, wrongStamp, false, default));
        Assert.False(await store.RecordAsync(account.Id, wrongStamp, true, default));
        Assert.Equal(1, (await account.State()).Count);
        await account.DisposeAsync();
        Assert.False(await store.RecordAsync(account.Id, wrongStamp, true, default));
    }

    [Fact]
    public async Task AttemptWriterCannotReadHashesChangeIdentityOrInsertDeleteAccounts()
    {
        await using var connection = new MySqlConnection(Settings().Writer); await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "SELECT password_hash FROM users LIMIT 1",
            "UPDATE users SET password_hash='denied' WHERE 1=0",
            "UPDATE users SET role='Admin' WHERE 1=0",
            "UPDATE users SET is_active=1 WHERE 1=0",
            "UPDATE users SET security_stamp=security_stamp WHERE 1=0",
            "INSERT INTO users(id) VALUES('00000000-0000-0000-0000-000000000000')",
            "DELETE FROM users WHERE 1=0"
        })
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            var denied = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains(denied.Number, new[] { 1142, 1143 });
        }
    }

    [Fact]
    public async Task WriterFailureAndCancellationFailClosedWithoutRawConnectionDetails()
    {
        var settings = Settings();
        var disconnected = new MySqlConnectionStringBuilder(settings.Writer) { Port = 1, Pooling = false };
        var store = new MySqlAuthenticationAttemptStore(Options.Create(new AuthenticationOptions { WriterConnectionString = disconnected.ConnectionString }));
        var failure = await Assert.ThrowsAsync<AuthenticationDependencyException>(() => store.RecordAsync(null, null, false, default));
        Assert.Null(failure.InnerException); Assert.Equal("Authentication dependency unavailable.", failure.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().RecordAsync(null, null, false, new CancellationToken(true)));
    }

    [Fact]
    public async Task RepeatedSuccessfulResetStillReportsMatchedRowAndUnavailableWriterPreventsLogin()
    {
        await using var account = await TestAccount.Create();
        Assert.True(await Store().RecordAsync(account.Id, account.Stamp, true, default));
        Assert.True(await Store().RecordAsync(account.Id, account.Stamp, true, default));
        var settings = Settings();
        var disconnected = new MySqlConnectionStringBuilder(settings.Writer) { Port = 1, Pooling = false };
        var failedWriter = new MySqlAuthenticationAttemptStore(Options.Create(new AuthenticationOptions { WriterConnectionString = disconnected.ConnectionString }));
        var backend = new MySqlAuthenticationBackend(new MySqlAuthenticationRepository(AuthOptions()), new IdentityPasswordVerifier(),
            TimeProvider.System, NullLogger<MySqlAuthenticationBackend>.Instance, failedWriter);
        var result = await backend.AuthenticateAsync(new LoginRequest { Username = account.Username, Password = account.Password });
        Assert.Equal(AuthenticationOutcome.Unavailable, result.Outcome); Assert.Null(result.User);
    }

    private static IOptions<AuthenticationOptions> AuthOptions()
    {
        var settings = Settings();
        return Options.Create(new AuthenticationOptions { ReaderConnectionString = settings.Connection, WriterConnectionString = settings.Writer });
    }
    private static MySqlAuthenticationAttemptStore Store() => new(AuthOptions());

    private static async Task AdminCommand((string Connection, string Writer, Account[] Accounts, string Private) settings, string sql, string? id = null, string? stamp = null)
    {
        var fields = File.ReadAllLines(Path.Combine(settings.Private, "admin.cnf")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        var connectionString = new MySqlConnectionStringBuilder(settings.Connection) { UserID = fields["user"], Password = fields["password"] };
        await using var connection = new MySqlConnection(connectionString.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("@id", id);
        if (stamp is not null) command.Parameters.AddWithValue("@stamp", stamp);
        await command.ExecuteNonQueryAsync(); // Test-only synthetic records on Settings-guarded loopback3319, never runtime/admin fallback.
    }

    private sealed class TestAccount : IAsyncDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Username { get; } = "be005.test." + Guid.NewGuid().ToString("N");
        public string Stamp { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private string hash = "";
        private bool disposed;
        public override string ToString() => "TestAccount [REDACTED]";
        public static async Task<TestAccount> Create()
        {
            var account = new TestAccount();
            var hasher = new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 220_000 }));
            account.hash = hasher.HashPassword("unused", account.Password);
            // All values are factory-controlled, but still parameterized; generated secrets never reach process arguments/logs.
            var settings = Settings();
            var fields = File.ReadAllLines(Path.Combine(settings.Private, "admin.cnf")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
            var builder = new MySqlConnectionStringBuilder(settings.Connection) { UserID = fields["user"], Password = fields["password"] };
            await using var connection = new MySqlConnection(builder.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO users(id,username,normalized_username,display_name,password_hash,is_active,security_stamp) VALUES(@id,@username,@normalized,'Synthetic lockout',@hash,1,@stamp)";
            command.Parameters.AddWithValue("@id", account.Id); command.Parameters.AddWithValue("@username", account.Username);
            command.Parameters.AddWithValue("@normalized", account.Username.ToUpperInvariant()); command.Parameters.AddWithValue("@hash", account.hash);
            command.Parameters.AddWithValue("@stamp", account.Stamp); await command.ExecuteNonQueryAsync();
            return account;
        }
        public async Task<(int Count, DateTimeOffset? End)> State()
        {
            await using var connection = new MySqlConnection(Settings().Writer); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT access_failed_count,lockout_end_utc FROM users WHERE id=@id";
            command.Parameters.AddWithValue("@id", Id); await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)));
        }
        public async Task<bool> HashUnchanged() => (await new MySqlAuthenticationRepository(AuthOptions()).FindAsync(Username.ToUpperInvariant(), default))!.PasswordHash == hash;
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            // Exact generated id and its known namespace; never broad cleanup or a Company record.
            await AdminCommand(Settings(), "DELETE FROM users WHERE id=@id AND username LIKE 'be005.test.%'", Id);
            disposed = true;
        }
    }
}
