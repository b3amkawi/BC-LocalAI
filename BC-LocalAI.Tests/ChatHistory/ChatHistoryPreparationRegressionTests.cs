using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

public sealed class ChatHistoryPreparationRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ChatMessage Message(long sequence = 1, MessageRole role = MessageRole.Assistant,
        MessageStatus status = MessageStatus.Pending, Guid? conversationId = null) =>
        new(Guid.NewGuid(), conversationId ?? ConversationId, sequence, role, "content", status, null, Now, Now);

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void TransitionRejectsUndefinedTargetStatus(int value) =>
        Assert.Throws<ChatHistoryValidationException>(() =>
            ChatHistoryRules.EnsureTransition(Message(), (MessageStatus)value, "text", null));

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void TransitionRejectsUndefinedCurrentStatus(int value) =>
        Assert.Throws<ChatHistoryValidationException>(() =>
            ChatHistoryRules.EnsureTransition(Message(status: (MessageStatus)value), MessageStatus.Streaming, "text", null));

    [Fact]
    public void ContextRejectsMessagesFromDifferentConversations() =>
        Assert.Throws<ArgumentException>(() => new ConversationContextBuilder().Build([
            Message(1, MessageRole.User, MessageStatus.Completed),
            Message(2, status: MessageStatus.Completed, conversationId: Guid.NewGuid())
        ], 100));

    [Fact]
    public void ContextRejectsDuplicateSequences() =>
        Assert.Throws<ArgumentException>(() => new ConversationContextBuilder().Build([
            Message(1, MessageRole.User, MessageStatus.Completed), Message(1, status: MessageStatus.Completed)
        ], 100));

    [Fact]
    public void ContextDoesNotOverflowWhenEstimatorReturnsVeryLargeCosts()
    {
        var builder = new ConversationContextBuilder(new FixedEstimator(int.MaxValue));
        var context = builder.Build([
            Message(1, MessageRole.User, MessageStatus.Completed), Message(2, status: MessageStatus.Completed)
        ], int.MaxValue);
        Assert.Empty(context.Messages);
        Assert.Equal(0, context.EstimatedTokens);
    }

    [Fact]
    public void ContextRejectsNegativeEstimates() =>
        Assert.Throws<ArgumentException>(() => new ConversationContextBuilder(new FixedEstimator(-1)).Build([
            Message(1, MessageRole.User, MessageStatus.Completed)
        ], 100));

    [Fact]
    public void ContextDoesNotIncludeAnAssistantWithoutItsUserQuestion()
    {
        var context = new ConversationContextBuilder().Build([
            Message(1, status: MessageStatus.Completed),
            Message(2, MessageRole.User, MessageStatus.Completed), Message(3, status: MessageStatus.Completed)
        ], 100);
        Assert.Equal([2L, 3L], context.Messages.Select(m => m.Sequence));
        Assert.Equal(1, context.DroppedMessages);
    }

    [Fact]
    public void PageCursorLengthIsBounded() =>
        Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.EnsureValidPage(new PageRequest(20, new string('A', 4096))));

    [Fact]
    public void PageRejectsNullInsteadOfThrowingNullReference() =>
        Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.EnsureValidPage(null!));

    [Fact]
    public async Task ReferenceRejectsUnknownMessageRoleWithoutChangingData()
    {
        var repository = new InMemoryChatHistoryRepository(() => Now);
        var conversation = await repository.CreateConversationAsync("owner", "title");
        await Assert.ThrowsAsync<ChatHistoryValidationException>(() =>
            repository.AddMessageAsync("owner", conversation.Id, (MessageRole)99, "text"));
        Assert.Empty((await repository.ListMessagesAsync("owner", conversation.Id, new PageRequest()))!.Items);
    }

    private sealed class FixedEstimator(int cost) : ITokenEstimator
    {
        public int Estimate(string text) => cost;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void MessageContentIsPreservedIncludingThaiEmojiAndLineBreaks(int repetitions)
    {
        var content = string.Concat(Enumerable.Repeat("ทดสอบ😀\n", repetitions));
        ChatHistoryRules.EnsureValidMessage(MessageRole.Assistant, content);
    }

    [Fact]
    public void MessageByteCeilingIncludesBoundaryButRejectsLargerUtf8Content()
    {
        ChatHistoryRules.EnsureValidMessage(MessageRole.User, new string('x', ChatHistoryRules.MaxMessageBytes));
        Assert.Throws<ChatHistoryValidationException>(() =>
            ChatHistoryRules.EnsureValidMessage(MessageRole.User, new string('x', ChatHistoryRules.MaxMessageBytes + 1)));
        Assert.Throws<ChatHistoryValidationException>(() =>
            ChatHistoryRules.EnsureValidMessage(MessageRole.User, new string('ก', ChatHistoryRules.MaxMessageBytes / 3 + 1)));
    }

    [Fact]
    public void IllFormedUtf16IsNotSilentlyReplacedWhenPersistingText()
    {
        Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.EnsureValidMessage(MessageRole.User, "\ud800"));
        Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.NormalizeTitle("\ud800"));
    }

    [Theory]
    [InlineData("internal error details")]
    [InlineData("AI_TIMEOUT\n")]
    public void ErrorCodeIsASafeBoundedCodeRatherThanProviderErrorText(string code) =>
        Assert.Throws<ChatHistoryValidationException>(() =>
            ChatHistoryRules.EnsureTransition(Message(), MessageStatus.Failed, "partial", code));

    [Fact]
    public void NullEstimatorIsRejected() => Assert.Throws<ArgumentNullException>(() => new ConversationContextBuilder(null!));

    [Fact]
    public async Task MessageUpdateAdvancesConversationActivity()
    {
        var now = Now;
        var repository = new InMemoryChatHistoryRepository(() => now);
        var conversation = await repository.CreateConversationAsync("owner", "title");
        var message = await repository.AddMessageAsync("owner", conversation.Id, MessageRole.Assistant, "");
        now = now.AddMinutes(1);
        await repository.UpdateMessageAsync("owner", message!.Id, "done", MessageStatus.Completed);
        Assert.Equal(now, (await repository.GetConversationAsync("owner", conversation.Id))!.UpdatedAt);
    }
}
