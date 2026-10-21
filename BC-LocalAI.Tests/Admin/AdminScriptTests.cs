using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using BcAdmin;
using MySqlConnector;
using Sec001Fixtures;

namespace BC_LocalAI.Tests.Admin;

/// <summary>SEC-003 admin account script, without a database. Live behaviour is in tests/SEC003.AdminTests.</summary>
public sealed class AdminScriptTests
{
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    // ---- MySQL privileges -------------------------------------------------------------------------------------------

    [Fact]
    public void WriterGrants_AreColumnScoped_OnUsersOnly_AndNothingOnTheAuditLog()
    {
        var grants = AdminWriterGrants.Create("bc_admin_writer", "10.0.0.5");

        Assert.Equal(
        [
            "GRANT SELECT (id, normalized_username) ON users TO 'bc_admin_writer'@'10.0.0.5';",
            "GRANT INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO 'bc_admin_writer'@'10.0.0.5';",
            "GRANT UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO 'bc_admin_writer'@'10.0.0.5';"
        ], grants);
        var all = string.Join('\n', grants);
        foreach (var forbidden in new[] { "DELETE", "ALL", "*", "GRANT OPTION", "DROP", "ALTER", "CREATE", "FILE", "SUPER", "admin_audit_log", "%" })
            Assert.DoesNotContain(forbidden, all, StringComparison.Ordinal);
        // The writer can never read a hash, a stamp, the status or the role; the audit log is written by the triggers only.
        Assert.DoesNotContain("password_hash", grants[0]);
        Assert.DoesNotContain("security_stamp", grants[0]);
        Assert.All(grants, grant => Assert.Contains(" ON users TO ", grant));
    }

    [Fact]
    public void WriterAccount_RequiresTls_AndLocksItselfAfterFailedLogins()
    {
        Assert.Equal("ALTER USER 'bc_admin_writer'@'10.0.0.0/255.255.255.0' REQUIRE SSL WITH MAX_USER_CONNECTIONS 4 FAILED_LOGIN_ATTEMPTS 5 PASSWORD_LOCK_TIME 1;",
            AdminWriterGrants.AccountSettings("bc_admin_writer", "10.0.0.0/255.255.255.0"));
    }

    [Fact]
    public void GrantsFile_ForTheDba_IsExactlyWhatTheScriptPrints()
    {
        var file = File.ReadAllLines(Path.Combine(RepoRoot(), "database", "sec003", "admin-writer-grants.sql"))
            .Where(line => line.Length > 0 && !line.StartsWith("--")).Select(line => line.Replace("<admin-host>", "10.0.0.5"));

        Assert.Equal([AdminWriterGrants.AccountSettings("bc_admin_writer", "10.0.0.5"), .. AdminWriterGrants.Create("bc_admin_writer", "10.0.0.5")], file);
    }

    [Theory]
    [InlineData("bc_admin_writer'@'%'; GRANT ALL ON *.* TO 'x", "127.0.0.1")]
    [InlineData("BC_ADMIN", "127.0.0.1")]
    [InlineData("bc_admin_writer", "host' OR '1")]
    [InlineData("", "127.0.0.1")]
    [InlineData("bc_admin_writer", "%")]
    [InlineData("bc_admin_writer", "10.0.0.%")]
    [InlineData("bc_admin_writer", "admin_pc")]
    public void WriterGrants_RefuseAnythingButAPlainAccountAndHost(string user, string host) =>
        Assert.Throws<ArgumentException>(() => AdminWriterGrants.Create(user, host));

    [Fact]
    public void Fixture_GivesTheAdminWriterExactlyTheDocumentedGrants_OverTls()
    {
        var sql = AdminWriterBootstrap.Create(Secret());

        Assert.StartsWith("CREATE USER 'bc_admin_writer'@'127.0.0.1' IDENTIFIED WITH caching_sha2_password BY '", sql[0]);
        Assert.Equal(AdminWriterGrants.AccountSettings("bc_admin_writer", "127.0.0.1"), sql[1]);
        Assert.Contains("REQUIRE SSL", sql[1]);
        Assert.Equal(AdminWriterGrants.Create("bc_admin_writer", "127.0.0.1"), sql.Skip(2));
        Assert.Throws<ArgumentException>(() => AdminWriterBootstrap.Create("x'; DROP USER root; --"));
    }

    [Fact]
    public void AuditSql_IsAdditive_StrictInnoDbUtf8_AndNeverStoresASecret()
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), "database", "sec003", "admin-audit.sql")).Replace("\r", "");
        var statements = string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--")));

        Assert.Contains("CREATE TABLE IF NOT EXISTS admin_audit_log", statements);
        Assert.Contains("ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_bin", statements);
        Assert.Contains("SET SESSION sql_mode = 'STRICT_ALL_TABLES,", statements); // triggers keep the mode they were created with
        Assert.Contains("CREATE TRIGGER IF NOT EXISTS trg_users_admin_audit_insert AFTER INSERT ON users", statements);
        Assert.Contains("CREATE TRIGGER IF NOT EXISTS trg_users_admin_audit_update AFTER UPDATE ON users", statements);
        foreach (var forbidden in new[] { "DROP", "DELETE", "TRUNCATE", "ALTER", "UPDATE users", "CURRENT_USER" })
            Assert.DoesNotContain(forbidden, statements, StringComparison.OrdinalIgnoreCase);
        // Audit rows take db_user from USER() on the server, and no VALUES list ever copies a hash or a stamp.
        var values = System.Text.RegularExpressions.Regex.Matches(statements, @"VALUES \(([^;]*?)\);").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(5, values.Length);
        Assert.All(values, v => Assert.Contains("USER()", v));
        Assert.All(values, v => Assert.DoesNotContain("password_hash", v));
        Assert.All(values, v => Assert.DoesNotContain("security_stamp", v));
        var table = statements[statements.IndexOf("CREATE TABLE", StringComparison.Ordinal)..statements.IndexOf("DELIMITER", StringComparison.Ordinal)];
        statements = table;
        // The exact column list ('reset_password' is only an action name), so a secret column cannot be added unnoticed.
        var columns = System.Text.RegularExpressions.Regex.Matches(statements, @"^\s+([a-z_]+) [A-Z]", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(["id", "occurred_at_utc", "actor", "os_user", "db_user", "action", "target_user_id", "detail"], columns);
    }

    // ---- Account rules ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("รหัสผ่านที่ยาวพอ😀 ใช้ได้")]
    [InlineData("  spaces around are kept  ")]
    public void HashPassword_ProducesTheOnlyFormatTheAppAccepts(string password)
    {
        var hash = AccountPolicy.HashPassword(password);
        var verifier = new IdentityPasswordVerifier();

        Assert.True(verifier.Verify(hash, password));
        Assert.False(verifier.Verify(hash, password + "x"));
        Assert.False(verifier.Verify(hash, password.Trim()) && password != password.Trim());
        Assert.NotEqual(hash, AccountPolicy.HashPassword(password)); // random salt
        Assert.DoesNotContain(password, hash);
    }

    [Theory]
    [InlineData("short1!", "too short")]
    [InlineData("            ", "only spaces")]
    [InlineData("alice.smith-is-my-password", "contains username")]
    public void CheckPassword_RefusesWeakOrInvalidPasswords_WithoutEchoingThem(string password, string because)
    {
        var reason = AccountPolicy.CheckPassword(password, "Alice.Smith");

        Assert.True(reason is not null, because);
        if (password.Trim().Length > 0) Assert.DoesNotContain(password.Trim(), reason!);
    }

    [Fact]
    public void CheckPassword_RefusesALoneSurrogate()
    {
        // Built at run time: xUnit turns a lone surrogate in an attribute into U+FFFD, which is valid text.
        var password = new string('\ud800', 1) + " lone surrogate 1234";
        Assert.Equal("Password is not valid text or is too long.", AccountPolicy.CheckPassword(password, "bob"));
    }

    [Fact]
    public void CheckPassword_AcceptsTheBoundaries()
    {
        Assert.Null(AccountPolicy.CheckPassword(new string('a', 15), "bob"));
        Assert.Null(AccountPolicy.CheckPassword(new string('a', 1024), "bob"));
        Assert.NotNull(AccountPolicy.CheckPassword(new string('a', 14), "bob"));
        Assert.NotNull(AccountPolicy.CheckPassword(new string('a', 1025), "bob"));
        Assert.NotNull(AccountPolicy.CheckPassword(new string('ก', 1024 + 1), "bob"));
    }

    [Fact]
    public void CheckPassword_CountsCodePoints_NotUtf16Units()
    {
        // 14 emoji are 28 UTF-16 units but only 14 characters: refused. 15 are accepted.
        Assert.NotNull(AccountPolicy.CheckPassword(string.Concat(Enumerable.Repeat("😀", 14)), "bob"));
        Assert.Null(AccountPolicy.CheckPassword(string.Concat(Enumerable.Repeat("😀", 15)), "bob"));
    }

    [Theory]
    [InlineData("bob", "my bob long password", true)]       // shorter than 4: substring allowed
    [InlineData("al", "AL", false)]                            // ... but still compared as the whole password (too short anyway)
    [InlineData("anna", "this-has-ANNA-inside", false)]       // 4 or more: contained anywhere, any case
    [InlineData("anna", "a perfectly fine passphrase", true)]
    public void CheckPassword_UsernameRule_AppliesFromFourCharacters(string username, string password, bool accepted) =>
        Assert.Equal(accepted, AccountPolicy.CheckPassword(password, username) is null);

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice.Smith_01-x")]
    [InlineData("")]
    [InlineData("alice smith")]
    [InlineData("อลิซ")]
    [InlineData("alice@corp")]
    [InlineData("' OR 1=1 --")]
    public void Usernames_FollowTheLoginRules_SoEveryCreatedAccountCanSignIn(string username)
    {
        var loginAccepts = AuthenticationRequestValidator.Validate(new LoginRequest { Username = username, Password = "p" }) is null;

        Assert.Equal(loginAccepts, AccountPolicy.IsValidUsername(username));
        Assert.True(AccountPolicy.IsValidUsername(new string('a', 128)));
        Assert.False(AccountPolicy.IsValidUsername(new string('a', 129)));
    }

    [Fact]
    public void Roles_AreExactlyUserAndAdmin()
    {
        Assert.True(AccountPolicy.IsValidRole("User"));
        Assert.True(AccountPolicy.IsValidRole("Admin"));
        foreach (var role in new[] { "admin", "ADMIN", "KnowledgeManager", "", " User", null })
            Assert.False(AccountPolicy.IsValidRole(role));
    }

    [Fact]
    public void SecurityStamp_Is64HexCharacters_AndNeverRepeats()
    {
        var stamps = Enumerable.Range(0, 50).Select(_ => AccountPolicy.NewSecurityStamp()).ToArray();
        Assert.All(stamps, stamp => Assert.Matches("^[0-9A-F]{64}$", stamp));
        Assert.Equal(50, stamps.Distinct().Count());
    }

    // ---- Credential file --------------------------------------------------------------------------------------------

    [Fact]
    public void DbConfig_LoadsAPrivateFile_WithTlsAndFoundRows()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc_test");

        var builder = new MySqlConnectionStringBuilder(DbConfig.Load(file.Path, allowLocalDevelopmentDatabase: true));

        Assert.Equal("bc_admin_writer", builder.UserID);
        Assert.Equal(file.Password, builder.Password);
        Assert.Equal(3318u, builder.Port);
        Assert.Equal("bc_test", builder.Database);
        Assert.Equal(MySqlSslMode.Required, builder.SslMode);
        Assert.False(builder.UseAffectedRows);
        Assert.False(builder.AllowLoadLocalInfile);
        Assert.True(builder.AllowUserVariables); // the audit triggers read @bc_admin_actor
        Assert.Equal("other", new MySqlConnectionStringBuilder(DbConfig.Load(file.Path, "other", true)).Database);
    }

    [Fact]
    public void DbConfig_Loopback_WithoutCertificateCheck_OnlyWhenExplicitlyALocalDevelopmentDatabase()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        Assert.Throws<AdminConfigException>(() => DbConfig.Load(file.Path));

        using var withCa = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc", extra: "ssl-ca=C:/certs/ca.pem");
        Assert.Equal(MySqlSslMode.VerifyFull, new MySqlConnectionStringBuilder(DbConfig.Load(withCa.Path)).SslMode);
    }

    [Fact]
    public void DbConfig_RemoteDatabase_NeedsCertificateVerification()
    {
        using var noCa = PrivateFile.Create("bc_admin_writer", "db.corp.local", database: "bc");
        Assert.Throws<AdminConfigException>(() => DbConfig.Load(noCa.Path));

        using var withCa = PrivateFile.Create("bc_admin_writer", "db.corp.local", database: "bc", extra: "ssl-ca=C:/certs/ca.pem");
        Assert.Equal(MySqlSslMode.VerifyFull, new MySqlConnectionStringBuilder(DbConfig.Load(withCa.Path)).SslMode);
    }

    [Theory]
    [InlineData("bc_auth_reader")]
    [InlineData("BC_AUTH_ATTEMPT_WRITER")]
    [InlineData("root")]
    [InlineData("bc_sec001_admin")]
    [InlineData("bc_history_rw")]
    [InlineData("bc_history_purge")]
    public void DbConfig_RefusesTheReaderAndOtherReservedAccounts(string user)
    {
        using var file = PrivateFile.Create(user, "127.0.0.1", database: "bc");
        var error = Assert.Throws<AdminConfigException>(() => DbConfig.Load(file.Path));
        Assert.DoesNotContain(file.Password, error.Message);
    }

    [Fact]
    public void DbConfig_RefusesAFileOthersCanRead()
    {
        if (!OperatingSystem.IsWindows()) return; // CI and the admin machines are Windows; the unix branch checks chmod bits.
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var security = new FileInfo(file.Path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file.Path).SetAccessControl(security);

        var error = Assert.Throws<AdminConfigException>(() => DbConfig.Load(file.Path));
        Assert.Contains("readable by other accounts", error.Message);
    }

    [Fact]
    public void DbConfig_RefusesAFileWithNoAccessList_WhichEveryoneCanRead()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-only concept (NULL DACL); see DbConfig_RefusesAFileOthersCanRead.
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        // A NULL DACL has no rules at all, so a check that only looks for foreign Allow rules finds nothing to refuse.
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm("D:NO_ACCESS_CONTROL", AccessControlSections.Access);
        new FileInfo(file.Path).SetAccessControl(security);

        var error = Assert.Throws<AdminConfigException>(() => DbConfig.Load(file.Path, allowLocalDevelopmentDatabase: true));
        Assert.Contains("readable by other accounts", error.Message);
    }

    [Fact]
    public void DbConfig_RefusesRelativeOrMissingPaths_AndMissingFields()
    {
        Assert.Throws<AdminConfigException>(() => DbConfig.Load("admin-writer.cnf"));
        Assert.Throws<AdminConfigException>(() => DbConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cnf")));
        using var noDatabase = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: null);
        Assert.Throws<AdminConfigException>(() => DbConfig.Load(noDatabase.Path, allowLocalDevelopmentDatabase: true));
    }

    [Fact]
    public void DbConfig_ReadsOnlyTheClientSection()
    {
        var fields = DbConfig.Parse(["[mysqld]", "user=server", "[client]", "# note", "user = bc_admin_writer", "password=a=b", "[other]", "host=x"]);
        Assert.Equal("bc_admin_writer", fields["user"]);
        Assert.Equal("a=b", fields["password"]);
        Assert.False(fields.ContainsKey("host"));
    }

    // ---- Command line -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--password")]
    [InlineData("--password=S3cretValue!!")]
    [InlineData("--PASSWORD")]
    [InlineData("--password-file")]
    public async Task PasswordAsArgument_IsRefused_BeforeAnythingElse_AndNotEchoed(string option)
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);

        var code = await run.Cli.RunAsync(["reset-password", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "alice", option, "S3cretValue!!"]);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("never accepted as arguments", run.Error);
        Assert.DoesNotContain("S3cretValue", run.Output + run.Error);
        Assert.Empty(run.Store.Calls);
    }

    [Fact]
    public async Task Create_WithStdinPassword_StoresOnlyAHash_AndPrintsNoSecret()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        const string password = "correct horse battery";
        var run = Run.For(file, stdin: password + "\n");

        var code = await run.Cli.RunAsync(["create", "--db-config", file.Path, "--local-dev-database", "--actor", "ผู้ดูแล", "--username", "new.user",
            "--display-name", "ผู้ใช้ใหม่", "--role", "Admin", "--password-stdin"]);

        Assert.Equal(ExitCodes.Ok, code);
        var created = Assert.Single(run.Store.Created);
        Assert.Equal(("new.user", "ผู้ใช้ใหม่", "Admin"), (created.Username, created.DisplayName, created.Role));
        Assert.True(new IdentityPasswordVerifier().Verify(created.PasswordHash, password));
        Assert.Equal(new AuditContext("ผู้ดูแล", @"CORP\operator"), run.Store.Audits.Single());
        Assert.Equal($"OK create user={FakeStore.CreatedId} role=Admin active=yes audit=42", run.Output.Trim());
        Assert.DoesNotContain(password, run.Output + run.Error);
        Assert.DoesNotContain(created.PasswordHash, run.Output + run.Error);
    }

    [Fact]
    public async Task Create_DefaultsToUserRole_AndAsksTwiceAtTheHiddenPrompt()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file, prompts: ["a long enough secret", "a long enough secret"]);

        Assert.Equal(ExitCodes.Ok, await run.Cli.RunAsync(["create", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--username", "u1", "--display-name", "U"]));
        Assert.Equal("User", run.Store.Created.Single().Role);
        Assert.Equal(2, run.Prompts.Asked);
    }

    [Theory]
    [InlineData(new[] { "a long enough secret", "a long enough secreT" }, "do not match")]
    [InlineData(new[] { "too short", "too short" }, "at least 15")]
    [InlineData(new string[0], "--password-stdin")]
    public async Task Create_RefusesABadOrMissingPassword_AndChangesNothing(string[] prompts, string expected)
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file, prompts: prompts);

        var code = await run.Cli.RunAsync(["create", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--username", "u1", "--display-name", "U"]);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(expected, run.Error);
        Assert.Empty(run.Store.Calls);
    }

    [Theory]
    [InlineData("disable", "Disable", null)]
    [InlineData("enable", "Enable", null)]
    [InlineData("revoke-sessions", "RevokeSessions", null)]
    [InlineData("set-role", "SetRole", "Admin")]
    public async Task Changes_GoToTheResolvedId_WithTheRightAction(string command, string actionName, string? role)
    {
        var action = Enum.Parse<AdminAction>(actionName);
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);
        string[] args = ["--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "Alice"];

        var code = await run.Cli.RunAsync([command, .. args, .. role is null ? Array.Empty<string>() : ["--role", role]]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("Alice", run.Store.Resolved.Single());
        Assert.Equal((FakeStore.ExistingId, action, role), run.Store.Changes.Single());
        Assert.Equal($"OK {command} user={FakeStore.ExistingId} sessions-revoked=yes audit=42", run.Output.Trim());
    }

    [Fact]
    public async Task ResetPassword_SendsAVerifiableHash_NeverThePassword()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        const string password = "brand new passphrase";
        var run = Run.For(file, stdin: password + "\r\n");

        Assert.Equal(ExitCodes.Ok, await run.Cli.RunAsync(["reset-password", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", FakeStore.ExistingId, "--password-stdin"]));

        var (_, action, value) = run.Store.Changes.Single();
        Assert.Equal(AdminAction.ResetPassword, action);
        Assert.NotEqual(password, value);
        Assert.True(new IdentityPasswordVerifier().Verify(value, password));
        Assert.DoesNotContain(password, run.Output + run.Error);
    }

    [Fact]
    public async Task UnknownAccount_Is3_AndNothingIsChanged()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);

        Assert.Equal(ExitCodes.NotFound, await run.Cli.RunAsync(["disable", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "nobody"]));
        Assert.Empty(run.Store.Changes);
        Assert.Contains("Nothing changed", run.Error);
    }

    [Theory]
    [InlineData("disable", "--user", "alice")] // no actor
    [InlineData("disable", "--actor", "a")] // no user
    [InlineData("disable", "--actor", "a", "--user", "alice", "--role", "Admin")] // option not for this command
    [InlineData("set-role", "--actor", "a", "--user", "alice", "--role", "admin")] // roles are case-sensitive
    [InlineData("set-role", "--actor", "a", "--user", "alice")]
    [InlineData("disable", "--actor", "a", "--user", "alice", "--password-stdin")]
    [InlineData("disable", "--actor", "a", "--user", "' OR 1=1 --")]
    [InlineData("disable", "--actor", "a", "--user", "alice", "--user", "bob")]
    [InlineData("disable", "--actor", "a", "--user", "alice", "--force")]
    [InlineData("delete", "--actor", "a", "--user", "alice")]
    [InlineData("create", "--actor", "a", "--username", "bad name", "--display-name", "x", "--password-stdin")]
    public async Task InvalidCommandLines_Are2_AndTouchNothing(params string[] args)
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file, stdin: "a long enough secret\n");

        Assert.Equal(ExitCodes.Usage, await run.Cli.RunAsync([args[0], "--db-config", file.Path, "--local-dev-database", .. args.Skip(1)]));
        Assert.Empty(run.Store.Calls);
    }

    [Fact]
    public async Task ReaderCredentials_AreRefused_BeforeAnyDatabaseCall()
    {
        using var file = PrivateFile.Create("bc_auth_reader", "127.0.0.1", database: "bc");
        var run = Run.For(file);

        Assert.Equal(ExitCodes.Database, await run.Cli.RunAsync(["revoke-sessions", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "alice"]));
        Assert.Contains("separate admin writer", run.Error);
        Assert.Empty(run.Store.Calls);
    }

    [Fact]
    public async Task UnexpectedErrors_ShowOnlyTheExceptionType()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);
        run.Store.Throw = new InvalidOperationException($"Server=127.0.0.1;Password={file.Password}");  // ci-allow-secret: generated fake value

        Assert.Equal(ExitCodes.Unexpected, await run.Cli.RunAsync(["disable", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "alice"]));
        Assert.Contains("InvalidOperationException", run.Error);
        Assert.DoesNotContain(file.Password, run.Output + run.Error);
    }

    [Fact]
    public async Task ResetPassword_ById_StillRefusesAPasswordContainingTheUsername()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file, stdin: "alice-was-here-2026\n"); // the fake account's normalized username is ALICE

        Assert.Equal(ExitCodes.Usage, await run.Cli.RunAsync(["reset-password", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", FakeStore.ExistingId, "--password-stdin"]));
        Assert.Contains("must not contain the username", run.Error);
        Assert.Empty(run.Store.Changes);
    }

    [Fact]
    public async Task PasswordStdin_AtAConsole_IsRefused_SoThePasswordIsNotEchoed()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file, stdin: "a long enough secret\n", terminal: true);

        Assert.Equal(ExitCodes.Usage, await run.Cli.RunAsync(["create", "--db-config", file.Path, "--local-dev-database", "--actor", "a",
            "--username", "u1", "--display-name", "U", "--password-stdin"]));
        Assert.Contains("needs piped input", run.Error);
        Assert.Empty(run.Store.Calls);
    }

    [Fact]
    public async Task ConnectionLostDuringCommit_Is6_AndNeverSaysNothingChanged()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);
        run.Store.ThrowOnChange = new AdminOutcomeUnknownException();

        Assert.Equal(ExitCodes.OutcomeUnknown, await run.Cli.RunAsync(["disable", "--db-config", file.Path, "--local-dev-database", "--actor", "a", "--user", "alice"]));
        Assert.Contains("may or may not be saved", run.Error);
        Assert.DoesNotContain("Nothing changed", run.Error);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("invalid-operation")]
    [InlineData("canceled")]
    public async Task CommitFailure_InTheRealStore_BecomesOutcomeUnknown(string failure)
    {
        // The real wrapper MySqlAdminAccountStore uses around COMMIT (round-two review S3): not the fake store.
        Exception error = failure switch
        {
            "io" => new IOException("connection reset"),
            "timeout" => new TimeoutException(),
            "invalid-operation" => new InvalidOperationException("connection closed"),
            _ => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<AdminOutcomeUnknownException>(() => MySqlAdminAccountStore.CommitOrOutcomeUnknownAsync(() => throw error));
    }

    [Fact]
    public async Task CommitSuccess_IsReturnedUnchanged_AndOtherBugsAreNotHidden()
    {
        var committed = false;
        await MySqlAdminAccountStore.CommitOrOutcomeUnknownAsync(() => { committed = true; return Task.CompletedTask; });
        Assert.True(committed);
        // A programming error is not a lost connection: it must surface as itself.
        await Assert.ThrowsAsync<NullReferenceException>(() => MySqlAdminAccountStore.CommitOrOutcomeUnknownAsync(() => throw new NullReferenceException()));
    }

    [Theory]
    [InlineData("bc_auth_reader")]
    [InlineData("bc_history_rw")]
    [InlineData("root")]
    public async Task PrintGrants_RefusesToGrantAReservedAccount(string user)
    {
        var run = Run.For(null);

        Assert.Equal(ExitCodes.Usage, await run.Cli.RunAsync(["print-grants", "--writer-host", "10.0.0.5", "--writer-user", user]));
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task LoopbackWithoutTheLocalDevFlag_IsRefused_BeforeAnyDatabaseCall()
    {
        using var file = PrivateFile.Create("bc_admin_writer", "127.0.0.1", database: "bc");
        var run = Run.For(file);

        Assert.Equal(ExitCodes.Database, await run.Cli.RunAsync(["disable", "--db-config", file.Path, "--actor", "a", "--user", "alice"]));
        Assert.Contains("ssl-ca", run.Error);
        Assert.Empty(run.Store.Calls);
    }

    [Fact]
    public async Task PrintGrants_PrintsTheGrantsAndAPlaceholderOnly()
    {
        var run = Run.For(null);

        Assert.Equal(ExitCodes.Ok, await run.Cli.RunAsync(["print-grants", "--writer-host", "10.0.0.5"]));
        var lines = run.Output.Trim().Split(Environment.NewLine);
        Assert.StartsWith("-- ", lines[0]); // CREATE USER with a placeholder is never printed as a runnable statement
        Assert.Contains("BY '<secret>'", lines[0]);
        Assert.Equal(AdminWriterGrants.AccountSettings("bc_admin_writer", "10.0.0.5"), lines[1]);
        Assert.Equal(AdminWriterGrants.Create("bc_admin_writer", "10.0.0.5"), lines.Skip(2));
        Assert.Equal(ExitCodes.Usage, await Run.For(null).Cli.RunAsync(["print-grants", "--writer-host", "x'; DROP"]));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BC-LocalAI.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>A throwaway option file readable only by the current user, holding a fake generated password.</summary>
    private sealed class PrivateFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sec003-{Guid.NewGuid():N}.cnf");
        public string Password { get; } = Secret();

        public static PrivateFile Create(string user, string host, string? database, string? extra = null)
        {
            var file = new PrivateFile();
            File.WriteAllText(file.Path, $"[client]\nuser={user}\npassword={file.Password}\nhost={host}\nport=3318\n" +
                (database is null ? "" : $"database={database}\n") + (extra is null ? "" : extra + "\n"));
            if (OperatingSystem.IsWindows())
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(file.Path).SetAccessControl(security);
            }
            else File.SetUnixFileMode(file.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return file;
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class FakePrompts(string[] answers) : IPasswordSource
    {
        public int Asked { get; private set; }
        public string? ReadHidden(string prompt) => Asked < answers.Length ? answers[Asked++] : null;
    }

    private sealed class FakeStore : IAdminAccountStore
    {
        public const string ExistingId = "11111111-2222-3333-4444-555555555555";
        public const string CreatedId = "99999999-2222-3333-4444-555555555555";
        public List<string> Calls { get; } = [];
        public List<string> Resolved { get; } = [];
        public List<NewAccount> Created { get; } = [];
        public List<(string Id, AdminAction Action, string? Value)> Changes { get; } = [];
        public List<AuditContext> Audits { get; } = [];
        public Exception? Throw { get; set; }

        public Task<AccountRef?> ResolveAsync(string idOrUsername, CancellationToken cancellationToken)
        {
            Calls.Add("resolve");
            if (Throw is not null) throw Throw;
            Resolved.Add(idOrUsername);
            return Task.FromResult(idOrUsername is "nobody" ? null : new AccountRef(ExistingId, "ALICE"));
        }

        public Task<AdminResult> CreateAsync(NewAccount account, AuditContext audit, CancellationToken cancellationToken)
        {
            Calls.Add("create");
            Created.Add(account);
            Audits.Add(audit);
            return Task.FromResult(new AdminResult(true, CreatedId, 42));
        }

        public Exception? ThrowOnChange { get; set; }

        public Task<AdminResult> ChangeAsync(string userId, AdminAction action, string? value, AuditContext audit, CancellationToken cancellationToken)
        {
            Calls.Add("change");
            if (ThrowOnChange is not null) throw ThrowOnChange;
            Changes.Add((userId, action, value));
            Audits.Add(audit);
            return Task.FromResult(new AdminResult(true, userId, 42));
        }
    }

    private sealed class Run
    {
        private readonly StringWriter output = new(), error = new();
        public AdminCli Cli { get; }
        public FakeStore Store { get; } = new();
        public FakePrompts Prompts { get; }
        public string Output => output.ToString();
        public string Error => error.ToString();

        private Run(PrivateFile? file, string stdin, string[] prompts, bool terminal)
        {
            Prompts = new FakePrompts(prompts);
            Cli = new AdminCli(new StringReader(stdin), output, error, Prompts, connection =>
            {
                // The store only ever receives the connection built from the private file.
                Assert.Equal(file!.Password, new MySqlConnectionStringBuilder(connection).Password);
                return Store;
            }, @"CORP\operator", terminal);
        }

        public static Run For(PrivateFile? file, string stdin = "", string[]? prompts = null, bool terminal = false) => new(file, stdin, prompts ?? [], terminal);
    }
}
