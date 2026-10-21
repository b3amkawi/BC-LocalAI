using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

// Every test in this suite uses the same live fixture accounts: run them one at a time so one test's account change
// never lands in the middle of another test's login.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BE005.MySqlTests;

/// <summary>
/// BE-006 against the real isolated MySQL fixture: an account changed in the database while its owner is signed in
/// loses access at the next request, through the real login (BE-005) and the real MySQL state source (BE-006).
/// Same rules as the BE-005 suite: only the dedicated BE-005 loopback fixture (be005-mysql, port 3319), set by BE005_MYSQL_PRIVATE_DIRECTORY.
/// Every database change is undone in finally.
/// </summary>
public sealed class Be006LiveSessionTests
{
    private sealed record Account(string Username, string Password, string State);

    private static (string Reader, string Writer, string Admin, Account Active, string Private) Settings()
    {
        var directory = Environment.GetEnvironmentVariable("BE005_MYSQL_PRIVATE_DIRECTORY") ??
            throw new InvalidOperationException("Set BE005_MYSQL_PRIVATE_DIRECTORY to the isolated fixture private directory.");
        Dictionary<string, string> Read(string file) => File.ReadAllLines(Path.Combine(directory, file))
            .Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var reader = Read("reader.cnf");
        var writer = Read("writer.cnf");
        var admin = Read("admin.cnf");
        if (reader["host"] != "127.0.0.1" || reader["port"] != "3319" || writer["port"] != "3319" || admin["port"] != "3319"
            || writer["user"] != "bc_auth_attempt_writer")
            throw new InvalidOperationException("Only the dedicated BE-005 loopback fixture at port 3319 is allowed.");
        string Connection(Dictionary<string, string> fields) => new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = 3319, UserID = fields["user"], Password = fields["password"],
            Database = "bc_localai_sec001", SslMode = MySqlSslMode.Required, ConnectionTimeout = 3, MaximumPoolSize = 4
        }.ConnectionString;
        var accounts = JsonSerializer.Deserialize<Account[]>(File.ReadAllText(Path.Combine(directory, "test-accounts.json")))!;
        return (Connection(reader), Connection(writer), Connection(admin), accounts.Single(a => a.State == "active"), directory);
    }

    private static WebApplicationFactory<Program> App(string reader, string writer, string keys) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
                ["Authentication:ReaderConnectionString"] = reader, ["Authentication:WriterConnectionString"] = writer,
                ["Authentication:KeyDirectory"] = keys
            }));
        });

    private static async Task<HttpResponseMessage> Login(HttpClient client, Account account, string? password = null)
    {
        using var csrf = await client.GetAsync("/api/auth/csrf");
        using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = account.Username, password = password ?? account.Password })
        };
        request.Headers.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private static async Task<int> Admin(string admin, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("stamp")]
    [InlineData("role")]
    public async Task ChangingTheAccountInMySql_EndsTheOpenSession_AndItStaysEnded(string change)
    {
        var (reader, writer, admin, active, keys) = Settings();
        string? originalStamp = null;
        await using (var connection = new MySqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT security_stamp FROM users WHERE username = @u";
            read.Parameters.AddWithValue("@u", active.Username);
            originalStamp = (string?)await read.ExecuteScalarAsync();
        }

        await using var factory = App(reader, writer, keys);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        try
        {
            using (var login = await Login(client, active)) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

            var sql = change switch
            {
                "disable" => "UPDATE users SET is_active = 0 WHERE username = @u",
                "stamp" => "UPDATE users SET security_stamp = REPEAT('B', 64) WHERE username = @u",
                _ => "UPDATE users SET role = 'Admin' WHERE username = @u"
            };
            Assert.Equal(1, await Admin(admin, sql, ("@u", active.Username)));

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
        finally
        {
            await Admin(admin, "UPDATE users SET is_active = 1, role = 'User', security_stamp = @s WHERE username = @u",
                ("@s", originalStamp!), ("@u", active.Username));
        }

        // Account restored: the old session does not come back; a fresh login works again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var again = await Login(client, active);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswordsFromSomeoneElse_LockNewSignIns_ButDoNotSignTheOwnerOut()
    {
        var (reader, writer, admin, active, keys) = Settings();
        await using var factory = App(reader, writer, keys);
        // The attacker comes through another app host on the same database, so BE-005's in-process rate limiter of the
        // owner's host is not what stops them: the lockout persisted in MySQL is.
        await using var elsewhere = App(reader, writer, keys);
        var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false };
        using var owner = factory.CreateClient(options);
        using var attacker = elsewhere.CreateClient(options);
        try
        {
            using (var login = await Login(owner, active)) Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            for (var i = 0; i < 5; i++)
            {
                using var failure = await Login(attacker, active, active.Password + "-wrong");
                Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            }

            // Locked in MySQL by BE-005 (real attempt writer): a new sign-in with the right password is refused, even from a
            // fresh host whose in-process rate limiter has seen nothing ...
            await using (var third = App(reader, writer, keys))
            {
                using var fresh = third.CreateClient(options);
                using var again = await Login(fresh, active);
                Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
            }
            // ... but the owner, signed in before, keeps working.
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/auth/me")).StatusCode);
        }
        finally
        {
            await Admin(admin, "UPDATE users SET access_failed_count = 0, lockout_end_utc = NULL WHERE username = @u", ("@u", active.Username));
        }
    }
}
