using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>
/// The behaviour every <see cref="IChatHistoryRepository"/> must have (BE-007). A database implementation must inherit this class
/// and pass the same tests, so ownership, IDOR protection, pagination and message states cannot drift.
/// </summary>
public abstract class ChatHistoryRepositoryContractTests
{
    protected const string Alice = "employee-alice";
    protected const string Bob = "employee-bob";

    /// <param name="clock">Time source the repository must use, so ordering tests are deterministic.</param>
    protected abstract IChatHistoryRepository CreateRepository(Func<DateTimeOffset> clock);

    private DateTimeOffset _now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private IChatHistoryRepository Repo() => CreateRepository(() => _now);

    private void Tick() => _now = _now.AddSeconds(1);

    // ---------------------------------------------------------------- conversations

    [Fact]
    public async Task Create_StoresTheOwnerAndANormalizedTitle()
    {
        var repo = Repo();

        var created = await repo.CreateConversationAsync(Alice, "  สรุปรายงานประจำเดือน  ");

        Assert.Equal(Alice, created.UserId);
        Assert.Equal("สรุปรายงานประจำเดือน", created.Title);
        Assert.Equal(created, await repo.GetConversationAsync(Alice, created.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithoutATitle_UsesTheDefault(string title)
    {
        var created = await Repo().CreateConversationAsync(Alice, title);

        Assert.Equal(ChatHistoryRules.DefaultTitle, created.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Create_WithoutAUser_IsRefused(string userId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Repo().CreateConversationAsync(userId, "t"));
    }

    [Fact]
    public async Task List_ReturnsOnlyTheUsersOwnConversations_NewestFirst()
    {
        var repo = Repo();
        var first = await repo.CreateConversationAsync(Alice, "first");
        Tick();
        await repo.CreateConversationAsync(Bob, "bob's");
        Tick();
        var second = await repo.CreateConversationAsync(Alice, "second");

        var page = await repo.ListConversationsAsync(Alice, new PageRequest());

        Assert.Equal([second.Id, first.Id], page.Items.Select(c => c.Id));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task List_PagesThroughEverything_WithoutGapsOrDuplicates()
    {
        var repo = Repo();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add((await repo.CreateConversationAsync(Alice, $"c{i}")).Id);
            if (i % 2 == 0) Tick(); // some conversations share a timestamp: the id breaks the tie
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await repo.ListConversationsAsync(Alice, new PageRequest(3, cursor));
            seen.AddRange(page.Items.Select(c => c.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(ids.Order(), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task AMessage_MovesTheConversationToTheTop()
    {
        var repo = Repo();
        var old = await repo.CreateConversationAsync(Alice, "old");
        Tick();
        await repo.CreateConversationAsync(Alice, "new");
        Tick();

        await repo.AddMessageAsync(Alice, old.Id, MessageRole.User, "hello again");

        var page = await repo.ListConversationsAsync(Alice, new PageRequest());
        Assert.Equal(old.Id, page.Items[0].Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageRequest.MaxLimit + 1)]
    public async Task List_RejectsAnInvalidLimit(int limit)
    {
        var thrown = await Assert.ThrowsAsync<ChatHistoryValidationException>(
            () => Repo().ListConversationsAsync(Alice, new PageRequest(limit)));

        Assert.Equal("INVALID_PAGE", thrown.Code);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("aGVsbG8=")] // "hello"
    [InlineData("OTk5OTk5OTk5OTk5OTk5OTk5OXx4")] // out-of-range ticks
    public async Task List_RejectsAForgedCursor(string cursor)
    {
        var thrown = await Assert.ThrowsAsync<ChatHistoryValidationException>(
            () => Repo().ListConversationsAsync(Alice, new PageRequest(10, cursor)));

        Assert.Equal("INVALID_PAGE", thrown.Code);
    }

    [Fact]
    public async Task Rename_ChangesTheTitle()
    {
        var repo = Repo();
        var created = await repo.CreateConversationAsync(Alice, "draft");

        var renamed = await repo.RenameConversationAsync(Alice, created.Id, "Final title");

        Assert.Equal("Final title", renamed!.Title);
        Assert.Equal("Final title", (await repo.GetConversationAsync(Alice, created.Id))!.Title);
    }

    [Fact]
    public async Task Rename_ValidatesTheTitle()
    {
        var repo = Repo();
        var created = await repo.CreateConversationAsync(Alice, "draft");

        await Assert.ThrowsAsync<ChatHistoryValidationException>(
            () => repo.RenameConversationAsync(Alice, created.Id, new string('x', ChatHistoryRules.MaxTitleLength + 1)));
        Assert.Equal("draft", (await repo.GetConversationAsync(Alice, created.Id))!.Title);
    }

    [Fact]
    public async Task Delete_HidesTheConversationAndItsMessages()
    {
        var repo = Repo();
        var created = await repo.CreateConversationAsync(Alice, "to delete");
        await repo.AddMessageAsync(Alice, created.Id, MessageRole.User, "secret question");

        Assert.True(await repo.DeleteConversationAsync(Alice, created.Id));

        Assert.Null(await repo.GetConversationAsync(Alice, created.Id));
        Assert.Null(await repo.ListMessagesAsync(Alice, created.Id, new PageRequest()));
        Assert.Empty((await repo.ListConversationsAsync(Alice, new PageRequest())).Items);
        Assert.Null(await repo.AddMessageAsync(Alice, created.Id, MessageRole.User, "more"));
        Assert.False(await repo.DeleteConversationAsync(Alice, created.Id)); // already gone
    }

    // ---------------------------------------------------------------- ownership (IDOR)

    [Fact]
    public async Task AnotherUser_CannotSeeChangeOrUseAConversation_AndCannotTellItExists()
    {
        var repo = Repo();
        var alices = await repo.CreateConversationAsync(Alice, "private");
        var question = await repo.AddMessageAsync(Alice, alices.Id, MessageRole.User, "private question");
        var answer = await repo.AddMessageAsync(Alice, alices.Id, MessageRole.Assistant, "");
        var missing = Guid.NewGuid();

        // Bob gets exactly what he gets for an id that does not exist.
        Assert.Null(await repo.GetConversationAsync(Bob, alices.Id));
        Assert.Null(await repo.GetConversationAsync(Bob, missing));
        Assert.Null(await repo.RenameConversationAsync(Bob, alices.Id, "hacked"));
        Assert.Null(await repo.RenameConversationAsync(Bob, missing, "hacked"));
        Assert.False(await repo.DeleteConversationAsync(Bob, alices.Id));
        Assert.False(await repo.DeleteConversationAsync(Bob, missing));
        Assert.Null(await repo.ListMessagesAsync(Bob, alices.Id, new PageRequest()));
        Assert.Null(await repo.AddMessageAsync(Bob, alices.Id, MessageRole.User, "injected"));
        Assert.Null(await repo.UpdateMessageAsync(Bob, answer!.Id, "forged", MessageStatus.Completed));
        Assert.Null(await repo.UpdateMessageAsync(Bob, question!.Id, "forged", MessageStatus.Completed));
        Assert.Empty((await repo.ListConversationsAsync(Bob, new PageRequest())).Items);

        // Nothing of Alice's changed.
        var after = await repo.GetConversationAsync(Alice, alices.Id);
        Assert.Equal("private", after!.Title);
        var messages = (await repo.ListMessagesAsync(Alice, alices.Id, new PageRequest()))!.Items;
        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageStatus.Pending, messages[1].Status);
    }

    [Fact]
    public async Task UserIdsAreComparedExactly()
    {
        var repo = Repo();
        var created = await repo.CreateConversationAsync(Alice, "t");

        Assert.Null(await repo.GetConversationAsync(Alice.ToUpperInvariant(), created.Id));
        Assert.Null(await repo.GetConversationAsync(Alice + " ", created.Id));
        Assert.Null(await repo.GetConversationAsync("", created.Id));
    }

    // ---------------------------------------------------------------- messages

    [Fact]
    public async Task Messages_KeepTheirOrder_AndThaiTextIsUnchanged()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");
        const string thai = "ภาษาไทย 😊 é ทดสอบ\nบรรทัดใหม่";

        var q = await repo.AddMessageAsync(Alice, c.Id, MessageRole.User, thai);
        var a = await repo.AddMessageAsync(Alice, c.Id, MessageRole.Assistant, "");
        await repo.UpdateMessageAsync(Alice, a!.Id, thai, MessageStatus.Completed);

        var items = (await repo.ListMessagesAsync(Alice, c.Id, new PageRequest()))!.Items;
        Assert.Equal([q!.Id, a.Id], items.Select(m => m.Id));
        Assert.True(items[0].Sequence < items[1].Sequence);
        Assert.All(items, m => Assert.Equal(thai, m.Content));
    }

    [Fact]
    public async Task UserMessages_AreCompleted_AssistantMessages_StartPending()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");

        var q = await repo.AddMessageAsync(Alice, c.Id, MessageRole.User, "hi");
        var a = await repo.AddMessageAsync(Alice, c.Id, MessageRole.Assistant, "");

        Assert.Equal(MessageStatus.Completed, q!.Status);
        Assert.Equal(MessageStatus.Pending, a!.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyUserMessage_IsRefused(string content)
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");

        await Assert.ThrowsAsync<ChatHistoryValidationException>(() => repo.AddMessageAsync(Alice, c.Id, MessageRole.User, content));
    }

    [Fact]
    public async Task StreamingThenCompleted_IsStored()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");
        var a = await repo.AddMessageAsync(Alice, c.Id, MessageRole.Assistant, "");

        await repo.UpdateMessageAsync(Alice, a!.Id, "part", MessageStatus.Streaming);
        await repo.UpdateMessageAsync(Alice, a.Id, "part and more", MessageStatus.Streaming);
        var done = await repo.UpdateMessageAsync(Alice, a.Id, "part and more.", MessageStatus.Completed);

        Assert.Equal(MessageStatus.Completed, done!.Status);
        Assert.Equal("part and more.", done.Content);
    }

    [Fact]
    public async Task AnInterruptedStream_KeepsItsPartialText_ButIsNeverCompleted()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");
        var a = await repo.AddMessageAsync(Alice, c.Id, MessageRole.Assistant, "");
        await repo.UpdateMessageAsync(Alice, a!.Id, "half an ans", MessageStatus.Streaming);

        var failed = await repo.UpdateMessageAsync(Alice, a.Id, "half an ans", MessageStatus.Failed, "AI_TIMEOUT");

        Assert.Equal(MessageStatus.Failed, failed!.Status);
        Assert.Equal("AI_TIMEOUT", failed.ErrorCode);
        await Assert.ThrowsAsync<ChatHistoryValidationException>(
            () => repo.UpdateMessageAsync(Alice, a.Id, "half an answer", MessageStatus.Completed));
    }

    [Fact]
    public async Task ARefusedTransition_LeavesTheMessageUnchanged()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");
        var a = await repo.AddMessageAsync(Alice, c.Id, MessageRole.Assistant, "");
        await repo.UpdateMessageAsync(Alice, a!.Id, "done", MessageStatus.Completed);

        await Assert.ThrowsAsync<ChatHistoryValidationException>(
            () => repo.UpdateMessageAsync(Alice, a.Id, "changed later", MessageStatus.Streaming));

        var stored = (await repo.ListMessagesAsync(Alice, c.Id, new PageRequest()))!.Items.Single();
        Assert.Equal("done", stored.Content);
        Assert.Equal(MessageStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task Messages_PageInOrder()
    {
        var repo = Repo();
        var c = await repo.CreateConversationAsync(Alice, "t");
        for (var i = 0; i < 5; i++)
        {
            await repo.AddMessageAsync(Alice, c.Id, MessageRole.User, $"m{i}");
        }

        var first = (await repo.ListMessagesAsync(Alice, c.Id, new PageRequest(2)))!;
        var second = (await repo.ListMessagesAsync(Alice, c.Id, new PageRequest(2, first.NextCursor)))!;
        var third = (await repo.ListMessagesAsync(Alice, c.Id, new PageRequest(2, second.NextCursor)))!;

        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], first.Items.Concat(second.Items).Concat(third.Items).Select(m => m.Content));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task Recent_ReturnsTheNewestMessagesInConversationOrder_OwnerOnly()
    {
        var repo = Repo();
        var conversation = await repo.CreateConversationAsync(Alice, "t");
        for (var i = 1; i <= 5; i++)
        {
            Tick();
            await repo.AddMessageAsync(Alice, conversation.Id, MessageRole.User, $"m{i}");
        }

        var recent = await repo.GetRecentMessagesAsync(Alice, conversation.Id, 3);

        Assert.Equal(["m3", "m4", "m5"], recent!.Select(m => m.Content));
        Assert.Null(await repo.GetRecentMessagesAsync(Bob, conversation.Id, 3));
        await repo.DeleteConversationAsync(Alice, conversation.Id);
        Assert.Null(await repo.GetRecentMessagesAsync(Alice, conversation.Id, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ChatHistoryRules.MaxRecentMessages + 1)]
    public async Task Recent_RejectsAnUnboundedCount(int count)
    {
        var repo = Repo();
        var conversation = await repo.CreateConversationAsync(Alice, "t");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.GetRecentMessagesAsync(Alice, conversation.Id, count));
    }

    [Fact]
    public async Task ACancelledCaller_ChangesNothing()
    {
        var repo = Repo();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.CreateConversationAsync(Alice, "t", cancelled.Token));

        Assert.Empty((await repo.ListConversationsAsync(Alice, new PageRequest())).Items);
    }
}
