using Sec001Fixtures;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>Grants of the two history accounts in the BE-007 fixture (runs in CI; the live tests check them on MySQL).</summary>
public sealed class ChatHistoryProvisioningTests
{
    private static string[] Sql() => ChatHistoryBootstrap.Create(new string('A', 64), "CREATE TABLE Conversations (Id INT);", new string('B', 64));

    [Fact]
    public void RuntimeAccount_ReadsAndWritesHistoryOnly_NeverDeletes()
    {
        var grants = Sql().Where(line => line.Contains($"TO '{ChatHistoryBootstrap.User}'", StringComparison.Ordinal)).ToArray();

        Assert.Equal(
        [
            $"GRANT SELECT, INSERT, UPDATE ON bc_localai_sec001.Conversations TO '{ChatHistoryBootstrap.User}'@'127.0.0.1';",
            $"GRANT SELECT, INSERT, UPDATE ON bc_localai_sec001.Messages TO '{ChatHistoryBootstrap.User}'@'127.0.0.1';"
        ], grants);
    }

    [Fact]
    public void PurgeAccount_ReadsAndDeletesHistory_AndOnlyThreeAccountColumns()
    {
        var grants = Sql().Where(line => line.Contains($"TO '{ChatHistoryBootstrap.PurgeUser}'", StringComparison.Ordinal)).ToArray();

        Assert.Equal(
        [
            $"GRANT SELECT, DELETE ON bc_localai_sec001.Conversations TO '{ChatHistoryBootstrap.PurgeUser}'@'127.0.0.1';",
            $"GRANT SELECT, DELETE ON bc_localai_sec001.Messages TO '{ChatHistoryBootstrap.PurgeUser}'@'127.0.0.1';",
            $"GRANT SELECT (id, is_active, updated_at_utc) ON bc_localai_sec001.users TO '{ChatHistoryBootstrap.PurgeUser}'@'127.0.0.1';"
        ], grants);
        var all = string.Join('\n', Sql());
        Assert.DoesNotContain("password_hash", all);
        Assert.DoesNotContain("ALL PRIVILEGES", all);
        Assert.DoesNotContain("WITH GRANT OPTION", all);
        Assert.Equal(2, Sql().Count(line => line.Contains("REQUIRE SSL", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("not-generated", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "x'; GRANT ALL ON *.*; --")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void UncontrolledOrSharedSecrets_AreRefused(string runtime, string purge) =>
        Assert.Throws<ArgumentException>(() => ChatHistoryBootstrap.Create(runtime, "", purge));
}
