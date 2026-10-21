using System.Text.RegularExpressions;

namespace BC_LocalAI.Tests.ChatHistory;

// Static DDL contract checks, NOT MySQL syntax/migration/integration tests.
public sealed class ChatHistorySchemaDraftTests
{
    private static string Read(string name) => string.Join('\n', File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "ChatHistorySchema", name)).Where(l => !l.TrimStart().StartsWith("--")));

    [Fact]
    public void OwnerIsCaseSensitiveAndNoPadRatherThanTheOldAiCiCollation()
    {
        var up = Read("001-initial.up.sql");
        Assert.Matches(@"UserId\s+VARCHAR\(64\)\s+CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin", up);
        Assert.DoesNotContain("utf8mb4_0900_ai_ci", up);
    }

    [Theory]
    [InlineData("IX_Conversations_User_Updated")]
    [InlineData("UX_Messages_Conversation_Sequence")]
    [InlineData("FK_Messages_Conversations")]
    [InlineData("CK_Messages_Sequence")]
    [InlineData("CK_Messages_Role")]
    [InlineData("CK_Messages_Status")]
    [InlineData("CK_Messages_UserState")]
    [InlineData("CK_Messages_CompletedContent")]
    [InlineData("CK_Messages_ErrorCode")]
    [InlineData("CK_Messages_Size")]
    public void DraftIncludesRequiredIndexesAndIntegrityGuards(string name) => Assert.Contains(name, Read("001-initial.up.sql"));

    [Fact]
    public void UpOnlyCreatesApplicationTablesAndDoesNotSilentlyAcceptExistingTables()
    {
        var up = Read("001-initial.up.sql");
        Assert.Equal(2, Regex.Matches(up, @"\bCREATE TABLE\b").Count);
        Assert.DoesNotMatch(@"(?i)\b(DROP|ALTER|TRUNCATE|GRANT|USE|CREATE DATABASE|IF NOT EXISTS)\b", up);
        Assert.Contains("ON DELETE RESTRICT", up);
    }

    [Fact]
    public void RollbackRequiresExplicitOptInAndRefusesNonEmptyTablesBeforeDrop()
    {
        var down = Read("001-initial.down.sql");
        Assert.Contains("COALESCE(@BE007_ALLOW_EMPTY_ROLLBACK, 0) <> 1", down);
        Assert.Contains("EXISTS (SELECT 1 FROM Messages LIMIT 1)", down);
        Assert.Contains("EXISTS (SELECT 1 FROM Conversations LIMIT 1)", down);
        Assert.Equal(2, Regex.Matches(down, "SIGNAL SQLSTATE '45000'").Count);
        Assert.True(down.IndexOf("DROP TABLE Messages", StringComparison.Ordinal) > down.IndexOf("SIGNAL SQLSTATE", StringComparison.Ordinal));
        Assert.True(down.IndexOf("DROP TABLE Messages", StringComparison.Ordinal) < down.IndexOf("DROP TABLE Conversations", StringComparison.Ordinal));
    }
}
