using System.Diagnostics;
using System.Text.Json;
using MySqlConnector;
using Xunit;

namespace BE007.MySqlTests;

/// <summary>
/// Retention purge (scripts/chat-history/purge.ps1) on the real fixture: deleted conversations after 30 days, closed accounts after
/// 90 days, everything else kept; one transaction; audit line without personal data; the purge account's limited rights.
/// </summary>
public sealed class Be007PurgeTests : IAsyncLifetime
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private DateTime _closedAccountOriginal;
    private DateTime _lockedAccountOriginal;
    private string _closed = "";
    private string _active = "";
    private string _locked = "";

    public async Task InitializeAsync()
    {
        await Be007Fixture.ClearHistory();
        _active = await IdOf("active");
        _closed = await IdOf("disabled");
        _locked = await IdOf("locked");
        _closedAccountOriginal = (DateTime)(await Be007Fixture.AdminScalar("SELECT updated_at_utc FROM users WHERE id = @id", ("@id", _closed)))!;
        _lockedAccountOriginal = (DateTime)(await Be007Fixture.AdminScalar("SELECT updated_at_utc FROM users WHERE id = @id", ("@id", _locked)))!;
    }

    public async Task DisposeAsync()
    {
        await Be007Fixture.AdminExecute("UPDATE users SET updated_at_utc = @t WHERE id = @id", ("@t", _closedAccountOriginal), ("@id", _closed));
        await Be007Fixture.AdminExecute("UPDATE users SET updated_at_utc = @t WHERE id = @id", ("@t", _lockedAccountOriginal), ("@id", _locked));
        await Be007Fixture.ClearHistory();
    }

    private static async Task<string> IdOf(string state)
    {
        var username = Be007Fixture.AccountsByState(state).Username;
        return Convert.ToString(await Be007Fixture.AdminScalar("SELECT id FROM users WHERE username = @u", ("@u", username)))!; // CHAR(36) is read as a Guid
    }

    /// <summary>A conversation (with <paramref name="messages"/> messages) whose times are set relative to the server clock.</summary>
    private static async Task<Guid> Conversation(string owner, int updatedDaysAgo, int? deletedDaysAgo = null, int messages = 2)
    {
        var id = Guid.NewGuid();
        await Be007Fixture.AdminExecute(
            "INSERT INTO Conversations (Id, UserId, Title, CreatedAt, UpdatedAt, DeletedAt, NextSequence) VALUES (@id, @user, 'private title', " +
            "UTC_TIMESTAMP(6) - INTERVAL @u DAY, UTC_TIMESTAMP(6) - INTERVAL @u DAY, " +
            "IF(@d IS NULL, NULL, UTC_TIMESTAMP(6) - INTERVAL @d DAY), @next)",
            ("@id", id.ToByteArray(bigEndian: true)), ("@user", owner), ("@u", updatedDaysAgo), ("@d", (object?)deletedDaysAgo ?? DBNull.Value),
            ("@next", messages + 1));
        for (var i = 1; i <= messages; i++)
        {
            await Be007Fixture.AdminExecute(
                "INSERT INTO Messages (Id, ConversationId, Sequence, Role, Content, Status, CreatedAt, UpdatedAt) " +
                "VALUES (@m, @c, @s, 'user', 'secret message text', 'completed', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
                ("@m", Guid.NewGuid().ToByteArray(bigEndian: true)), ("@c", id.ToByteArray(bigEndian: true)), ("@s", i));
        }

        return id;
    }

    private static async Task<HashSet<Guid>> Remaining()
    {
        await using var connection = new MySqlConnection(Be007Fixture.Admin);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT Id FROM Conversations", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new HashSet<Guid>();
        while (await reader.ReadAsync()) ids.Add(new Guid((byte[])reader[0], bigEndian: true));
        return ids;
    }

    private static (int ExitCode, string Output) Script(params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(Repo, "scripts", "chat-history", "purge.ps1") }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string[] Target(string action) =>
    [
        "-Action", action, "-DefaultsFile", Path.Combine(Be007Fixture.Directory, "purge.cnf"),
        "-Database", "bc_localai_sec001", "-ApprovedTarget", "127.0.0.1:3320/bc_localai_sec001"
    ];

    [Fact]
    public async Task PreviewCountsOnly_PurgeErasesExactlyThePolicy_AndAuditsWithoutPersonalData()
    {
        await Be007Fixture.AdminExecute("UPDATE users SET updated_at_utc = UTC_TIMESTAMP(6) - INTERVAL 91 DAY WHERE id = @id", ("@id", _closed));
        // An ACTIVE (only locked) account unchanged for 200 days: its history must stay.
        await Be007Fixture.AdminExecute("UPDATE users SET updated_at_utc = UTC_TIMESTAMP(6) - INTERVAL 200 DAY WHERE id = @id", ("@id", _locked));
        var deleted31 = await Conversation(_active, 40, deletedDaysAgo: 31);
        var deleted29 = await Conversation(_active, 40, deletedDaysAgo: 29);
        var oldButActive = await Conversation(_active, 1000);
        var closed1 = await Conversation(_closed, 5);
        var closed2 = await Conversation(_closed, 300, messages: 3);
        var lockedButActive = await Conversation(_locked, 200);
        var ownerGone91 = await Conversation("00000000-0000-4000-8000-00000000dead", 91);
        var ownerGone10 = await Conversation("00000000-0000-4000-8000-00000000beef", 10);
        var all = await Remaining();

        var preview = Script(Target("Preview"));

        Assert.Equal(0, preview.ExitCode);
        Assert.Contains("deleted > 30 days: 1 conversations / 2 messages", preview.Output);
        Assert.Contains("closed accounts > 90 days: 3 conversations / 7 messages", preview.Output);
        Assert.Equal(all, await Remaining());   // preview changes nothing

        var audit = Path.Combine(Path.GetTempPath(), $"be007-purge-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var purge = Script([.. Target("Purge"), "-AuditLog", audit]);

            Assert.Equal(0, purge.ExitCode);
            Assert.Equal(new HashSet<Guid> { deleted29, oldButActive, lockedButActive, ownerGone10 }, await Remaining());
            Assert.Equal(8L, await Be007Fixture.AdminScalar("SELECT COUNT(*) FROM Messages"));   // 4 kept conversations x 2
            Assert.Equal(0L, await Be007Fixture.AdminScalar("SELECT COUNT(*) FROM Messages m LEFT JOIN Conversations c ON c.Id = m.ConversationId WHERE c.Id IS NULL"));
            _ = (deleted31, closed1, closed2, ownerGone91);

            var line = Assert.Single(File.ReadAllLines(audit));
            using var json = JsonDocument.Parse(line);
            Assert.Equal((1, 2, 3, 7), (json.RootElement.GetProperty("deleted_conversations").GetInt32(), json.RootElement.GetProperty("deleted_messages").GetInt32(),
                json.RootElement.GetProperty("closed_account_conversations").GetInt32(), json.RootElement.GetProperty("closed_account_messages").GetInt32()));
            foreach (var personal in new[] { _active, _closed, "private title", "secret message text", "password" })
                Assert.DoesNotContain(personal, line);

            // Running again finds nothing more.
            Assert.Contains("deleted > 30 days: 0 conversations / 0 messages; closed accounts > 90 days: 0 conversations / 0 messages",
                Script(Target("Preview")).Output);
        }
        finally
        {
            File.Delete(audit);
        }
    }

    [Fact]
    public async Task AnAccountClosedLessThan90DaysAgo_KeepsItsHistory()
    {
        await Be007Fixture.AdminExecute("UPDATE users SET updated_at_utc = UTC_TIMESTAMP(6) - INTERVAL 89 DAY WHERE id = @id", ("@id", _closed));
        await Conversation(_closed, 400);

        Assert.Contains("closed accounts > 90 days: 0 conversations / 0 messages", Script(Target("Preview")).Output);
    }

    [Fact]
    public async Task Refusals_ChangeNothing()
    {
        await Conversation(_active, 40, deletedDaysAgo: 31);
        var before = await Remaining();

        var noAudit = Script(Target("Purge"));
        var badAuditFolder = Script([.. Target("Purge"), "-AuditLog", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "a.jsonl")]);
        var wrongTarget = Script("-Action", "Purge", "-DefaultsFile", Path.Combine(Be007Fixture.Directory, "purge.cnf"), "-Database", "bc_localai_sec001",
            "-ApprovedTarget", "10.0.0.5:3306/bc_localai_sec001", "-AuditLog", Path.Combine(Path.GetTempPath(), "x.jsonl"));

        Assert.All([noAudit, badAuditFolder, wrongTarget], r => Assert.NotEqual(0, r.ExitCode));
        Assert.Contains("Nothing was changed", noAudit.Output);
        Assert.Contains("Nothing was changed", badAuditFolder.Output);
        Assert.Contains("not the approved target", wrongTarget.Output);
        Assert.Equal(before, await Remaining());
    }

    [Theory]
    [InlineData("SELECT password_hash FROM users", 1143)]       // column-level: only id / is_active / updated_at_utc
    [InlineData("SELECT security_stamp FROM users", 1143)]
    [InlineData("UPDATE Conversations SET Title = 'x' WHERE 1 = 0", 1142)]
    [InlineData("INSERT INTO Messages (Id) VALUES (0x00)", 1142)]
    [InlineData("DELETE FROM users WHERE 1 = 0", 1142)]
    [InlineData("DROP TABLE Messages", 1142)]
    public async Task ThePurgeAccount_CanOnlyReadAndDeleteHistory(string sql, int error)
    {
        await using var connection = new MySqlConnection(Be007Fixture.Connection("purge.cnf"));
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(error, exception.Number);
    }
}
