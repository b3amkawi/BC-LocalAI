using BC_LocalAI.Configuration;
using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;
using BC_LocalAI.Tests.ChatHistory;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

namespace BE007.MySqlTests;

/// <summary>The shared repository contract (ownership, IDOR, paging, message states) against real MySQL.</summary>
public sealed class MySqlChatHistoryRepositoryTests : ChatHistoryRepositoryContractTests, IAsyncLifetime
{
    protected override IChatHistoryRepository CreateRepository(Func<DateTimeOffset> clock) => Be007Fixture.Repository(clock);

    public Task InitializeAsync() => Be007Fixture.ClearHistory();

    public Task DisposeAsync() => Be007Fixture.ClearHistory();
}

/// <summary>What only a real database can show: locking, durability, constraints, grants and failures.</summary>
public sealed class MySqlChatHistoryStorageTests : IAsyncLifetime
{
    private const string Alice = "00000000-0000-4000-8000-00000000a11c";

    public Task InitializeAsync() => Be007Fixture.ClearHistory();

    public Task DisposeAsync() => Be007Fixture.ClearHistory();

    [Fact]
    public async Task ConcurrentMessages_GetUniqueGaplessSequenceNumbers()
    {
        var repo = Be007Fixture.Repository();
        var conversation = await repo.CreateConversationAsync(Alice, "parallel");

        // 24 writers on separate connections at once: the row lock must serialize them.
        var added = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            Be007Fixture.Repository().AddMessageAsync(Alice, conversation.Id, MessageRole.User, $"message {i}")));

        Assert.All(added, message => Assert.NotNull(message));
        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), added.Select(m => m!.Sequence).Order());
        Assert.Equal(25L, await Be007Fixture.AdminScalar("SELECT NextSequence FROM Conversations WHERE Id = @id",
            ("@id", conversation.Id.ToByteArray(bigEndian: true))));
    }

    [Fact]
    public async Task DataSurvivesANewConnectionPool_AndThaiTextIsByteForByteTheSame()
    {
        const string thai = "สวัสดีครับ 😀 ก้ำ\nบรรทัดสอง";
        Guid id;
        {
            var repo = Be007Fixture.Repository();
            id = (await repo.CreateConversationAsync(Alice, "ทดสอบ")).Id;
            await repo.AddMessageAsync(Alice, id, MessageRole.User, thai);
        }

        MySqlConnection.ClearAllPools();
        var again = await Be007Fixture.Repository().ListMessagesAsync(Alice, id, new PageRequest());

        Assert.Equal(thai, Assert.Single(again!.Items).Content);
    }

    [Fact]
    public async Task IdsAreStoredInRfcByteOrder_AndTimesInUtcMicroseconds()
    {
        var at = new DateTimeOffset(2026, 10, 21, 9, 30, 15, TimeSpan.Zero).AddTicks(1234567); // 0.1234567 s
        var repo = Be007Fixture.Repository(() => at);
        var conversation = await repo.CreateConversationAsync(Alice, "t");

        var hex = (string?)await Be007Fixture.AdminScalar("SELECT HEX(Id) FROM Conversations");
        var stored = (DateTime)(await Be007Fixture.AdminScalar("SELECT UpdatedAt FROM Conversations"))!;

        Assert.Equal(conversation.Id.ToString("N").ToUpperInvariant(), hex);
        Assert.Equal(new DateTime(2026, 10, 21, 9, 30, 15).AddTicks(1234560), stored);
        Assert.Equal(stored, conversation.UpdatedAt.UtcDateTime);   // what the API returns is what the database holds
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesACompletedAnswerWithoutText_AndAFailedOneWithoutACode()
    {
        var repo = Be007Fixture.Repository();
        var conversation = await repo.CreateConversationAsync(Alice, "t");
        var answer = (await repo.AddMessageAsync(Alice, conversation.Id, MessageRole.Assistant, ""))!;

        // Bypassing the C# rules on purpose: the CHECK constraints are the last line of defence.
        await using var connection = new MySqlConnection(Be007Fixture.History);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE Messages SET Status = 'completed', Content = '  ' WHERE Id = @id",
                     "UPDATE Messages SET Status = 'failed', ErrorCode = NULL WHERE Id = @id",
                     "UPDATE Messages SET Status = 'done' WHERE Id = @id"
                 })
        {
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@id", answer.Id.ToByteArray(bigEndian: true));
            var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(3819, error.Number); // ER_CHECK_CONSTRAINT_VIOLATED
        }
    }

    [Theory]
    [InlineData("DELETE FROM Messages WHERE 1 = 0")]
    [InlineData("SELECT COUNT(*) FROM users")]
    [InlineData("DROP TABLE Messages")]
    [InlineData("ALTER TABLE Conversations ADD COLUMN x INT")]
    public async Task TheRuntimeAccount_CannotDeleteReadAccountsOrChangeTheSchema(string sql)
    {
        await using var connection = new MySqlConnection(Be007Fixture.History);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(MySqlErrorCode.TableAccessDenied, error.ErrorCode);
    }

    [Fact]
    public async Task DatabaseDown_IsTheSafeUnavailableError_WithoutSqlOrConnectionDetails()
    {
        var builder = new MySqlConnectionStringBuilder(Be007Fixture.History) { Port = 3399, ConnectionTimeout = 1 };
        var repo = new MySqlChatHistoryRepository(
            Options.Create(new ChatHistoryOptions { Enabled = true, ConnectionString = builder.ConnectionString }), TimeProvider.System);

        var error = await Assert.ThrowsAsync<ChatHistoryUnavailableException>(() => repo.CreateConversationAsync(Alice, "t"));

        Assert.Null(error.InnerException);
        Assert.DoesNotContain("3399", error.Message);
        Assert.DoesNotContain(builder.Password, error.ToString());
    }

    [Fact]
    public async Task AWriteWithAStaleStatus_IsNotOverwritten()
    {
        var repo = Be007Fixture.Repository();
        var conversation = await repo.CreateConversationAsync(Alice, "t");
        var answer = (await repo.AddMessageAsync(Alice, conversation.Id, MessageRole.Assistant, ""))!;
        await repo.UpdateMessageAsync(Alice, answer.Id, "final", MessageStatus.Completed);

        // A second writer that still thinks the answer is streaming must not reopen or change it.
        await Assert.ThrowsAsync<ChatHistoryValidationException>(() =>
            repo.UpdateMessageAsync(Alice, answer.Id, "late chunk", MessageStatus.Streaming));

        var stored = (await repo.ListMessagesAsync(Alice, conversation.Id, new PageRequest()))!.Items.Single();
        Assert.Equal(("final", MessageStatus.Completed), (stored.Content, stored.Status));
    }
}
