using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

public sealed class ConversationContextBuilderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Every message costs exactly its length, so budgets in the tests are easy to read.</summary>
    private sealed class LengthEstimator : ITokenEstimator
    {
        public int Estimate(string text) => text.Length;
    }

    private static readonly ConversationContextBuilder Builder = new(new LengthEstimator());

    private static long _sequence;

    private static ChatMessage U(string text) => M(MessageRole.User, text, MessageStatus.Completed);

    private static ChatMessage A(string text, MessageStatus status = MessageStatus.Completed) => M(MessageRole.Assistant, text, status);

    private static ChatMessage M(MessageRole role, string text, MessageStatus status) =>
        new(Guid.NewGuid(), Guid.Empty, Interlocked.Increment(ref _sequence), role, text, status, null, T, T);

    private static string[] Texts(ConversationContext context) => context.Messages.Select(m => m.Content).ToArray();

    [Fact]
    public void EverythingFits_EverythingIsKept_InOrder()
    {
        var history = new[] { U("q1"), A("a1"), U("q2"), A("a2") };

        var context = Builder.Build(history, 100);

        Assert.Equal(["q1", "a1", "q2", "a2"], Texts(context));
        Assert.Equal(8, context.EstimatedTokens);
        Assert.Equal(0, context.DroppedMessages);
    }

    [Fact]
    public void InputOrder_DoesNotMatter_TheSequenceDoes()
    {
        var history = new[] { U("q1"), A("a1"), U("q2"), A("a2") };

        var context = Builder.Build(history.Reverse(), 100);

        Assert.Equal(["q1", "a1", "q2", "a2"], Texts(context));
    }

    [Fact]
    public void OverBudget_DropsTheOldestTurnsFirst_AndKeepsQuestionAndAnswerTogether()
    {
        var history = new[] { U("q1"), A("a1"), U("q2"), A("a2"), U("q3"), A("a3") };

        var context = Builder.Build(history, 9); // room for two turns (4 + 4), not three

        Assert.Equal(["q2", "a2", "q3", "a3"], Texts(context));
        Assert.Equal(8, context.EstimatedTokens);
        Assert.Equal(2, context.DroppedMessages);
    }

    [Fact]
    public void NeverExceedsTheBudget()
    {
        var history = Enumerable.Range(0, 50).SelectMany(i => new[] { U($"question {i}"), A($"answer number {i}") }).ToArray();

        foreach (var budget in new[] { 0, 1, 17, 30, 100, 333, 10_000 })
        {
            var context = Builder.Build(history, budget);
            Assert.InRange(context.EstimatedTokens, 0, budget);
            Assert.Equal(context.Messages.Sum(m => m.Content.Length), context.EstimatedTokens);
        }
    }

    [Fact]
    public void ATurnThatDoesNotFit_StopsTheContext_EvenIfOlderSmallTurnsWouldFit()
    {
        // The context must be contiguous: skipping the big middle turn would give the model a conversation with a hole in it.
        var history = new[] { U("q"), A("a"), U("a very long question"), A("a very long answer"), U("q3"), A("a3") };

        var context = Builder.Build(history, 10);

        Assert.Equal(["q3", "a3"], Texts(context));
    }

    [Fact]
    public void ZeroBudget_GivesNoContext()
    {
        var context = Builder.Build([U("q1"), A("a1")], 0);

        Assert.Empty(context.Messages);
        Assert.Equal(2, context.DroppedMessages);
    }

    [Theory]
    [InlineData(MessageStatus.Pending)]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Failed)]
    [InlineData(MessageStatus.Cancelled)]
    public void IncompleteAnswers_AreNeverContext(MessageStatus status)
    {
        var history = new[] { U("q1"), A("partial answer", status), U("q2"), A("a2") };

        var context = Builder.Build(history, 1000);

        Assert.Equal(["q1", "q2", "a2"], Texts(context));
        Assert.DoesNotContain(context.Messages, m => m.Status != MessageStatus.Completed);
    }

    [Fact]
    public void AnAnswerWithoutAQuestion_IsExcluded()
    {
        var history = new[] { A("greeting"), U("q1"), A("a1") };

        var context = Builder.Build(history, 100);

        Assert.Equal(["q1", "a1"], Texts(context));
        Assert.Equal(1, context.DroppedMessages);
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => Builder.Build(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder.Build([], -1));
    }

    // ---------------------------------------------------------------- default estimator

    [Theory]
    [InlineData("", 4)]
    [InlineData("abc", 5)]          // 3 bytes -> 1 token + overhead
    [InlineData("abcd", 6)]         // 4 bytes -> 2 tokens (rounded up)
    [InlineData("สวัสดี", 10)]      // 6 Thai characters = 18 bytes -> 6 tokens
    [InlineData("😊", 6)]           // 4 bytes -> 2 tokens
    public void Utf8Estimator_ProducesDraftHeuristicEstimates(string text, int expected) =>
        Assert.Equal(expected, new Utf8TokenEstimator().Estimate(text));

    [Fact]
    public void Utf8Estimator_OverestimatesOnlyThisThaiSample()
    {
        // Measured with qwen3:4b (BE-003 live run): a 384-character Thai answer used 222 tokens.
        var thai = new string('ก', 384);

        Assert.True(new Utf8TokenEstimator().Estimate(thai) >= 222);
    }

    [Fact]
    public void DefaultBuilder_UsesTheUtf8Estimator()
    {
        var context = new ConversationContextBuilder().Build([U("abc"), A("abc")], 10);

        Assert.Equal(10, context.EstimatedTokens);
    }
}
