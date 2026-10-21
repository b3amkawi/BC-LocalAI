using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BC_LocalAI.Services.Authentication;
using BcAdmin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

// Tests share one database and the audit log: run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SEC003.AdminTests;

/// <summary>
/// SEC-003 admin script against the real isolated fixture (sec003-mysql, loopback port 3318, set by SEC003_MYSQL_PRIVATE_DIRECTORY),
/// with the real admin writer account and the real app for sign-in. Fails (never skips) without the fixture.
/// Each test only touches accounts it created (sec003.test.*); those accounts and their audit records are deleted in finally.
/// </summary>
public sealed class AdminScriptLiveTests : IAsyncLifetime
{
    private const string Database = "bc_localai_sec001";
    private static readonly WebApplicationFactoryClientOptions ClientOptions = new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false };
    private readonly Fixture settings = Fixture.Load();
    private readonly List<string> created = [];
    // Recorded before the account is created, so an assertion failing half-way through Create cannot leave it behind.
    private readonly List<string> usernames = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Exact generated usernames in the test namespace only; never a broad cleanup.
        foreach (var username in usernames)
        {
            await settings.Admin("DELETE a FROM admin_audit_log a JOIN users u ON u.id = a.target_user_id WHERE u.username = @u AND u.username LIKE 'sec003.test.%'", ("@u", username));
            await settings.Admin("DELETE FROM users WHERE username = @u AND username LIKE 'sec003.test.%'", ("@u", username));
        }
    }

    // ---- Each command, end to end ---------------------------------------------------------------------------------

    [Fact]
    public async Task Create_SignsInThroughTheRealApp_DisableEndsTheSession_EnableAllowsANewOne()
    {
        var (username, password) = await Create("User");
        var id = created.Single();
        var row = await settings.Row(id);
        Assert.Equal((true, "User", 0), (row.Active, row.Role, row.Failed));
        Assert.True(new IdentityPasswordVerifier().Verify(row.Hash, password));
        Assert.Equal(64, row.Stamp.Length);
        await AssertAudit(id, ("create", "role=User"));

        await using var app = App();
        using var client = app.CreateClient(ClientOptions);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, username, password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var disable = await Cli(["disable", "--user", username]);
        Assert.Equal(ExitCodes.Ok, disable.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var disabled = await settings.Row(id);
        Assert.False(disabled.Active);
        Assert.NotEqual(row.Stamp, disabled.Stamp);
        Assert.Equal(row.Hash, disabled.Hash);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, username, password)).StatusCode);

        Assert.Equal(ExitCodes.Ok, (await Cli(["enable", "--user", id])).Code);
        Assert.True((await settings.Row(id)).Active);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, username, password)).StatusCode);
        await AssertAudit(id, ("create", "role=User"), ("disable", null), ("enable", null));
    }

    [Fact]
    public async Task ResetPassword_EndsOpenSessions_OldPasswordStops_NewPasswordWorks()
    {
        var (username, oldPassword) = await Create("User");
        var id = created.Single();
        var before = await settings.Row(id);
        await using var app = App();
        using var session = app.CreateClient(ClientOptions);
        Assert.Equal(HttpStatusCode.OK, (await Login(session, username, oldPassword)).StatusCode);

        var newPassword = NewPassword();
        var reset = await Cli(["reset-password", "--user", username, "--password-stdin"], newPassword);

        Assert.Equal(ExitCodes.Ok, reset.Code);
        Assert.DoesNotContain(newPassword, reset.Output + reset.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
        var after = await settings.Row(id);
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.True(new IdentityPasswordVerifier().Verify(after.Hash, newPassword));
        Assert.Equal((before.Active, before.Role), (after.Active, after.Role));
        using var fresh = app.CreateClient(ClientOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(fresh, username, oldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(fresh, username, newPassword)).StatusCode);
        await AssertAudit(id, ("create", "role=User"), ("reset_password", null));
    }

    [Fact]
    public async Task SetRole_EndsTheSession_AndTheNextSignInHasTheNewRole()
    {
        var (username, password) = await Create("User");
        var id = created.Single();
        await using var app = App();
        using var client = app.CreateClient(ClientOptions);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, username, password)).StatusCode);

        Assert.Equal(ExitCodes.Ok, (await Cli(["set-role", "--user", username, "--role", "Admin"])).Code);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, username, password)).StatusCode);
        using var me = JsonDocument.Parse(await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        Assert.Equal(["Admin"], me.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        await AssertAudit(id, ("create", "role=User"), ("set_role", "role=Admin"));
    }

    [Fact]
    public async Task RevokeSessions_EndsEverySession_AndChangesOnlyTheStamp()
    {
        var (username, password) = await Create("Admin");
        var id = created.Single();
        await using var app = App();
        using var laptop = app.CreateClient(ClientOptions);
        using var phone = app.CreateClient(ClientOptions);
        Assert.Equal(HttpStatusCode.OK, (await Login(laptop, username, password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(phone, username, password)).StatusCode);
        var before = await settings.Row(id);

        Assert.Equal(ExitCodes.Ok, (await Cli(["revoke-sessions", "--user", id])).Code);

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        var after = await settings.Row(id);
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.Equal(before with { Stamp = after.Stamp }, after);
        await AssertAudit(id, ("create", "role=Admin"), ("revoke_sessions", null));
    }

    // ---- Audit ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditRecords_SayWhoWhatWhenAndWhom_AndHoldNoSecret()
    {
        var started = DateTime.UtcNow.AddSeconds(-5);
        var (_, password) = await Create("User");
        var id = created.Single();
        var row = await settings.Row(id);

        await using var connection = new MySqlConnection(settings.AdminConnection);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT * FROM admin_audit_log WHERE target_user_id = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => Convert.ToString(reader.GetValue(i))!);
        Assert.False(await reader.ReadAsync());

        Assert.Equal("ผู้ทดสอบ SEC-003", values["actor"]);
        Assert.Equal(EntryPoint.OsUser(), values["os_user"]);
        Assert.Equal("bc_admin_writer@127.0.0.1", values["db_user"]); // USER() in the trigger, not from the client
        Assert.Equal(("create", id, "role=User"), (values["action"], values["target_user_id"], values["detail"]));
        Assert.InRange(DateTime.Parse(values["occurred_at_utc"]), started, DateTime.UtcNow.AddSeconds(5));
        foreach (var value in values.Values)
            foreach (var secret in new[] { password, row.Hash, row.Stamp })
                Assert.DoesNotContain(secret, value);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("reset_password")]
    [InlineData("create")]
    public async Task WhenTheAuditRecordCannotBeWritten_NothingChanges(string action)
    {
        // An OS user name longer than the audit column makes the audit INSERT fail (strict mode) after the account change.
        var badAudit = new AuditContext("tester", new string('x', 300));
        var store = new MySqlAdminAccountStore(DbConfig.Load(settings.AdminWriterFile, Database, allowLocalDevelopmentDatabase: true));
        if (action == "create")
        {
            var username = "sec003.test." + Guid.NewGuid().ToString("N");
            usernames.Add(username); // cleaned up even if the rollback under test were broken
            var error = await Assert.ThrowsAsync<MySqlException>(() =>
                store.CreateAsync(new NewAccount(username, "x", "User", AccountPolicy.HashPassword(NewPassword())), badAudit, default));
            Assert.Equal(1406, error.Number);
            Assert.Equal(0L, await settings.Scalar("SELECT COUNT(*) FROM users WHERE username = @u", ("@u", username)));
            return;
        }

        await Create("User");
        var id = created.Single();
        var before = await settings.Row(id);
        var change = action == "disable"
            ? store.ChangeAsync(id, AdminAction.Disable, null, badAudit, default)
            : store.ChangeAsync(id, AdminAction.ResetPassword, AccountPolicy.HashPassword(NewPassword()), badAudit, default);

        Assert.Equal(1406, (await Assert.ThrowsAsync<MySqlException>(() => change)).Number);
        Assert.Equal(before, await settings.Row(id));
        await AssertAudit(id, ("create", "role=User"));
    }

    [Fact]
    public async Task ChangingAnAccountWithoutTheScript_IsStillAudited_WithTheRealDatabaseUser()
    {
        await Create("User");
        var id = created.Single();

        // Someone with the writer's credentials uses the mysql client directly and even claims to be another admin.
        await using (var connection = new MySqlConnection(DbConfig.Load(settings.AdminWriterFile, Database, allowLocalDevelopmentDatabase: true)))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "SET @bc_admin_actor = 'someone else'; UPDATE users SET is_active = 0, password_hash = 'x', security_stamp = REPEAT('C', 64) WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await using (var connection = new MySqlConnection(settings.AdminWriterConnectionWithoutVariables))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand("UPDATE users SET security_stamp = REPEAT('D', 64) WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await AssertAudit(id, ("create", "role=User"), ("reset_password", null), ("disable", null), ("revoke_sessions", null));
        var rows = await settings.Rows("SELECT actor, db_user FROM admin_audit_log WHERE target_user_id = @id AND action <> 'create' ORDER BY id", ("@id", id));
        Assert.Equal([("someone else", "bc_admin_writer@127.0.0.1"), ("someone else", "bc_admin_writer@127.0.0.1"), ("(direct SQL)", "bc_admin_writer@127.0.0.1")], rows);
    }

    [Fact]
    public async Task FailedLoginBookkeeping_ByTheAttemptWriter_IsNotAnAdminChange()
    {
        await Create("User");
        var id = created.Single();
        var before = await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log WHERE target_user_id = @id", ("@id", id));

        await using (var connection = new MySqlConnection(settings.AttemptWriterConnection))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "UPDATE users SET access_failed_count = access_failed_count + 1, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.Equal(before, await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log WHERE target_user_id = @id", ("@id", id)));
    }

    // ---- Refusals -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DuplicateUsername_Is4_AndAddsNothing()
    {
        var (username, _) = await Create("User");
        var audits = await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log");

        var again = await Cli(["create", "--username", username.ToUpperInvariant(), "--display-name", "dup", "--password-stdin"], NewPassword());

        Assert.Equal(ExitCodes.Conflict, again.Code);
        Assert.Contains("already exists", again.Error);
        Assert.Equal(1L, await settings.Scalar("SELECT COUNT(*) FROM users WHERE normalized_username = @u", ("@u", username.ToUpperInvariant())));
        Assert.Equal(audits, await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log"));
    }

    [Fact]
    public async Task UnknownAccount_Is3_AndWritesNoAudit()
    {
        var audits = await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log");

        Assert.Equal(ExitCodes.NotFound, (await Cli(["disable", "--user", "sec003.test.nobody"])).Code);
        Assert.Equal(ExitCodes.NotFound, (await Cli(["revoke-sessions", "--user", Guid.NewGuid().ToString()])).Code);
        Assert.Equal(audits, await settings.Scalar("SELECT COUNT(*) FROM admin_audit_log"));
    }

    [Theory]
    [InlineData("reader.cnf")]
    [InlineData("writer.cnf")]
    [InlineData("admin.cnf")]
    public async Task TheReaderAttemptWriterAndFixtureAdmin_CannotRunTheScript(string file)
    {
        var result = await Cli(["revoke-sessions", "--user", "SEC001.ACTIVE"], configFile: Path.Combine(settings.Directory, file));

        Assert.Equal(ExitCodes.Database, result.Code);
        Assert.Contains("separate admin writer", result.Error);
    }

    // ---- Least privilege, checked by MySQL itself -----------------------------------------------------------------

    [Fact]
    public async Task AdminWriter_HasExactlyTheDocumentedGrants()
    {
        await using var connection = new MySqlConnection(DbConfig.Load(settings.AdminWriterFile, Database, allowLocalDevelopmentDatabase: true));
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SHOW GRANTS FOR CURRENT_USER()", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var grants = new List<string>();
        while (await reader.ReadAsync()) grants.Add(reader.GetString(0));

        Assert.Equal(
        [
            "GRANT USAGE ON *.* TO `bc_admin_writer`@`127.0.0.1`",
            "GRANT SELECT (`id`, `normalized_username`), INSERT (`display_name`, `id`, `is_active`, `normalized_username`, `password_hash`, `role`, `security_stamp`, `updated_at_utc`, `username`), UPDATE (`is_active`, `password_hash`, `role`, `security_stamp`, `updated_at_utc`) ON `bc_localai_sec001`.`users` TO `bc_admin_writer`@`127.0.0.1`"
        ], grants.OrderBy(g => g.Contains("USAGE") ? 0 : 1)); // nothing at all on admin_audit_log
    }

    [Theory]
    [InlineData("SELECT password_hash FROM users LIMIT 1")]
    [InlineData("SELECT security_stamp FROM users LIMIT 1")]
    [InlineData("SELECT is_active, role FROM users LIMIT 1")]
    [InlineData("SELECT * FROM users LIMIT 1")]
    [InlineData("SELECT * FROM admin_audit_log LIMIT 1")]
    [InlineData("INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id) VALUES ('x', 'x', 'someone@else', 'create', 'x')")]
    [InlineData("UPDATE admin_audit_log SET actor = 'x' WHERE 1 = 0")]
    [InlineData("DELETE FROM admin_audit_log WHERE 1 = 0")]
    [InlineData("DELETE FROM users WHERE 1 = 0")]
    [InlineData("UPDATE users SET access_failed_count = 0 WHERE 1 = 0")]
    [InlineData("UPDATE users SET username = 'x' WHERE 1 = 0")]
    [InlineData("DROP TRIGGER trg_users_admin_audit_update")]
    [InlineData("SELECT * FROM sec003_probe_chats")]
    [InlineData("CREATE TABLE sec003_probe_new (id INT)")]
    [InlineData("SELECT user FROM mysql.user")]
    public async Task AdminWriter_IsRefusedEverythingElse_IncludingChatsAndTheAudit(string sql)
    {
        // A stand-in for a chat history table (BE-007): the writer has no grant on any table but users and the audit log.
        await settings.Admin("CREATE TABLE IF NOT EXISTS sec003_probe_chats (id INT PRIMARY KEY, body TEXT)");
        try
        {
            await using var connection = new MySqlConnection(DbConfig.Load(settings.AdminWriterFile, Database, allowLocalDevelopmentDatabase: true));
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            // 1142 = no privilege on the table, 1143 = not on that column.
            Assert.Contains((await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync())).Number, new[] { 1142, 1143 });
        }
        finally { await settings.Admin("DROP TABLE IF EXISTS sec003_probe_chats"); }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private async Task<(string Username, string Password)> Create(string role)
    {
        var username = "sec003.test." + Guid.NewGuid().ToString("N")[..12];
        var password = NewPassword();
        usernames.Add(username);
        var result = await Cli(["create", "--username", username, "--display-name", "บัญชีทดสอบ SEC-003", "--role", role, "--password-stdin"], password);
        Assert.Equal(ExitCodes.Ok, result.Code);
        Assert.DoesNotContain(password, result.Output + result.Error);
        // CHAR(36) ids come back as Guid from MySqlConnector.
        var id = Convert.ToString(await settings.ScalarObject("SELECT id FROM users WHERE username = @u", ("@u", username)))!;
        created.Add(id);
        Assert.StartsWith($"OK create user={id} role={role} active=yes audit=", result.Output.Trim());
        return (username, password);
    }

    private async Task<(int Code, string Output, string Error)> Cli(string[] args, string stdin = "", string? configFile = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new AdminCli(new StringReader(stdin + "\n"), output, error, new NoPrompt(),
            connection => new MySqlAdminAccountStore(connection), EntryPoint.OsUser());
        var code = await cli.RunAsync([args[0], "--db-config", configFile ?? settings.AdminWriterFile, "--database", Database, "--local-dev-database",
            "--actor", "ผู้ทดสอบ SEC-003", .. args.Skip(1)]);
        return (code, output.ToString(), error.ToString());
    }

    private async Task AssertAudit(string id, params (string Action, string? Detail)[] expected)
    {
        await using var connection = new MySqlConnection(settings.AdminConnection);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT action, detail FROM admin_audit_log WHERE target_user_id = @id ORDER BY id", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        var actual = new List<(string, string?)>();
        while (await reader.ReadAsync()) actual.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        Assert.Equal(expected, actual);
    }

    private WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
            ["Authentication:ReaderConnectionString"] = settings.ReaderConnection, ["Authentication:WriterConnectionString"] = settings.AttemptWriterConnection,
            ["Authentication:KeyDirectory"] = settings.Directory
        }));
    });

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string password)
    {
        using var csrf = await client.GetAsync("/api/auth/csrf");
        using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private static string NewPassword() => "Sec003-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private sealed class NoPrompt : IPasswordSource
    {
        public string? ReadHidden(string prompt) => null;
    }

    private sealed record Row(bool Active, string Role, string Hash, string Stamp, int Failed, DateTime? Lockout);

    private sealed record Fixture(string Directory, string AdminWriterFile, string ReaderConnection, string AttemptWriterConnection, string AdminConnection,
        string AdminWriterConnectionWithoutVariables)
    {
        public override string ToString() => "Fixture [REDACTED]";

        public static Fixture Load()
        {
            var directory = Environment.GetEnvironmentVariable("SEC003_MYSQL_PRIVATE_DIRECTORY") ??
                throw new InvalidOperationException("Set SEC003_MYSQL_PRIVATE_DIRECTORY to the isolated sec003-mysql fixture's private directory.");
            Dictionary<string, string> Read(string file) => DbConfig.Parse(File.ReadAllLines(Path.Combine(directory, file)));
            var files = new[] { "reader.cnf", "writer.cnf", "admin.cnf", "admin-writer.cnf" }.ToDictionary(f => f, Read);
            if (files.Values.Any(f => f["host"] != "127.0.0.1" || f["port"] != "3318") || files["admin-writer.cnf"]["user"] != "bc_admin_writer" ||
                files["writer.cnf"]["user"] != "bc_auth_attempt_writer")
                throw new InvalidOperationException("Only the dedicated SEC-003 loopback fixture at port 3318 is allowed.");
            string Connection(Dictionary<string, string> fields) => new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = 3318, UserID = fields["user"], Password = fields["password"], Database = Database,
                SslMode = MySqlSslMode.Required, ConnectionTimeout = 3, MaximumPoolSize = 4
            }.ConnectionString;
            return new Fixture(directory, Path.Combine(directory, "admin-writer.cnf"), Connection(files["reader.cnf"]),
                Connection(files["writer.cnf"]), Connection(files["admin.cnf"]), Connection(files["admin-writer.cnf"]));
        }

        public async Task Admin(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new MySqlConnection(AdminConnection);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarObject(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new MySqlConnection(AdminConnection);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync();
        }

        public async Task<List<(string, string)>> Rows(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new MySqlConnection(AdminConnection);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        }

        public async Task<long> Scalar(string sql, params (string Name, object Value)[] parameters) =>
            Convert.ToInt64(await ScalarObject(sql, parameters));

        public async Task<Row> Row(string id)
        {
            await using var connection = new MySqlConnection(AdminConnection);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "SELECT is_active, role, password_hash, security_stamp, access_failed_count, lockout_end_utc FROM users WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new Row(reader.GetBoolean(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5));
        }
    }
}
