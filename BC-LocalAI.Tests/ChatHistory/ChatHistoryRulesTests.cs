using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

public sealed class ChatHistoryRulesTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static ChatMessage Msg(MessageStatus status, MessageRole role = MessageRole.Assistant) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, role, "", status, null, T, T);

    // ---------------------------------------------------------------- title

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("  padded  ", "padded")]
    [InlineData("ภาษาไทย", "ภาษาไทย")]
    [InlineData(null, ChatHistoryRules.DefaultTitle)]
    [InlineData("", ChatHistoryRules.DefaultTitle)]
    [InlineData(" \t ", ChatHistoryRules.DefaultTitle)]
    public void NormalizeTitle_Accepts(string? input, string expected) =>
        Assert.Equal(expected, ChatHistoryRules.NormalizeTitle(input));

    [Fact]
    public void NormalizeTitle_AcceptsExactlyTheMaximumLength() =>
        Assert.Equal(ChatHistoryRules.MaxTitleLength, ChatHistoryRules.NormalizeTitle(new string('ก', ChatHistoryRules.MaxTitleLength)).Length);

    [Theory]
    [InlineData(ChatHistoryRules.MaxTitleLength + 1)]
    [InlineData(10_000)]
    public void NormalizeTitle_RejectsTooLong(int length)
    {
        var thrown = Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.NormalizeTitle(new string('x', length)));
        Assert.Equal("INVALID_TITLE", thrown.Code);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tinside")]
    [InlineData("null\0char")]
    [InlineData("bell\u0007")]
    public void NormalizeTitle_RejectsControlCharacters(string title) =>
        Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.NormalizeTitle(title));

    // ---------------------------------------------------------------- message state machine

    public static TheoryData<MessageStatus, MessageStatus> Allowed => new()
    {
        { MessageStatus.Pending, MessageStatus.Streaming },
        { MessageStatus.Pending, MessageStatus.Completed },
        { MessageStatus.Pending, MessageStatus.Failed },
        { MessageStatus.Pending, MessageStatus.Cancelled },
        { MessageStatus.Streaming, MessageStatus.Streaming },
        { MessageStatus.Streaming, MessageStatus.Completed },
        { MessageStatus.Streaming, MessageStatus.Failed },
        { MessageStatus.Streaming, MessageStatus.Cancelled },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Transition_Allowed(MessageStatus from, MessageStatus to)
    {
        var errorCode = to == MessageStatus.Failed ? "AI_TIMEOUT" : null;
        ChatHistoryRules.EnsureTransition(Msg(from), to, "text", errorCode);
    }

    public static TheoryData<MessageStatus, MessageStatus> Refused()
    {
        var data = new TheoryData<MessageStatus, MessageStatus>();
        foreach (var from in new[] { MessageStatus.Completed, MessageStatus.Failed, MessageStatus.Cancelled })
        {
            foreach (var to in Enum.GetValues<MessageStatus>())
            {
                data.Add(from, to); // final states never change
            }
        }

        data.Add(MessageStatus.Pending, MessageStatus.Pending);
        data.Add(MessageStatus.Streaming, MessageStatus.Pending);
        return data;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Transition_Refused(MessageStatus from, MessageStatus to)
    {
        var errorCode = to == MessageStatus.Failed ? "AI_TIMEOUT" : null;
        var thrown = Assert.Throws<ChatHistoryValidationException>(() => ChatHistoryRules.EnsureTransition(Msg(from), to, "text", errorCode));
        Assert.Equal("INVALID_MESSAGE_STATE", thrown.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Completed_NeedsContent(string content) =>
        Assert.Throws<ChatHistoryValidationException>(
            () => ChatHistoryRules.EnsureTransition(Msg(MessageStatus.Streaming), MessageStatus.Completed, content, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Failed_NeedsAnErrorCode(string? code) =>
        Assert.Throws<ChatHistoryValidationException>(
            () => ChatHistoryRules.EnsureTransition(Msg(MessageStatus.Streaming), MessageStatus.Failed, "", code));

    [Theory]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Completed)]
    [InlineData(MessageStatus.Cancelled)]
    public void OnlyFailed_HasAnErrorCode(MessageStatus to) =>
        Assert.Throws<ChatHistoryValidationException>(
            () => ChatHistoryRules.EnsureTransition(Msg(MessageStatus.Pending), to, "text", "AI_TIMEOUT"));

    [Fact]
    public void UserMessages_CannotBeUpdated() =>
        Assert.Throws<ChatHistoryValidationException>(
            () => ChatHistoryRules.EnsureTransition(Msg(MessageStatus.Pending, MessageRole.User), MessageStatus.Completed, "x", null));
}
