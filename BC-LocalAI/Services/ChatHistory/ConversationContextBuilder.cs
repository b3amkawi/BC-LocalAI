using System.Text;
using BC_LocalAI.Models.ChatHistory;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>Estimates how many model tokens a text uses.</summary>
public interface ITokenEstimator
{
    int Estimate(string text);
}

/// <summary>
/// Conservative estimate without a tokenizer: one token per 3 UTF-8 bytes, rounded up, plus a small per-message overhead.
/// Thai characters are 3 bytes, so Thai counts as about one token per character; qwen3:4b measured about 0.6 tokens per Thai
/// character, so the estimate errs on the safe side (the context is cut earlier, never later). Replace it when AI-004 or the
/// AI Engineer provides a real tokenizer count.
/// </summary>
public sealed class Utf8TokenEstimator : ITokenEstimator
{
    public const int PerMessageOverhead = 4;

    public int Estimate(string text) =>
        PerMessageOverhead + (Encoding.UTF8.GetByteCount(text ?? string.Empty) + 2) / 3;
}

/// <summary>Result of <see cref="ConversationContextBuilder.Build"/>.</summary>
public sealed record ConversationContext(IReadOnlyList<ChatMessage> Messages, int EstimatedTokens, int DroppedMessages);

/// <summary>
/// Chooses which earlier messages are sent to the model together with the new question (BE-007 D4, multi-turn context).
/// Rules:
/// <list type="bullet">
/// <item>only completed messages are used; pending, streaming, failed and cancelled messages are never context;</item>
/// <item>the newest messages are kept and the oldest are dropped first, so the result stays within the token budget;</item>
/// <item>a user question and the answer to it are kept or dropped together, so the model never sees an answer without its question;</item>
/// <item>the result is in conversation order (oldest first).</item>
/// </list>
/// How the context is sent to Ollama is not decided yet (the AI-002 contract sends one prompt); that is an open point for the AI Engineer.
/// </summary>
public sealed class ConversationContextBuilder(ITokenEstimator estimator)
{
    public ConversationContextBuilder() : this(new Utf8TokenEstimator())
    {
    }

    /// <param name="history">Earlier messages of the conversation (any order; sorted by <see cref="ChatMessage.Sequence"/>).</param>
    /// <param name="tokenBudget">Tokens available for the earlier messages (the new question is budgeted separately by the caller).</param>
    public ConversationContext Build(IEnumerable<ChatMessage> history, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);

        var completed = history
            .Where(m => m.Status == MessageStatus.Completed)
            .OrderBy(m => m.Sequence)
            .ToList();

        var turns = GroupIntoTurns(completed);
        var kept = new List<IReadOnlyList<ChatMessage>>();
        var used = 0;

        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var cost = turns[i].Sum(m => estimator.Estimate(m.Content));
            if (used + cost > tokenBudget)
            {
                break; // older turns are dropped too: the context must stay contiguous
            }

            used += cost;
            kept.Add(turns[i]);
        }

        kept.Reverse();
        var messages = kept.SelectMany(t => t).ToList();
        return new ConversationContext(messages, used, completed.Count - messages.Count);
    }

    /// <summary>A turn is a user question with the assistant answers that follow it; an answer with no question before it is a turn alone.</summary>
    private static List<IReadOnlyList<ChatMessage>> GroupIntoTurns(List<ChatMessage> messages)
    {
        var turns = new List<IReadOnlyList<ChatMessage>>();
        List<ChatMessage>? current = null;

        foreach (var message in messages)
        {
            if (message.Role == MessageRole.User || current is null)
            {
                current = [message];
                turns.Add(current);
            }
            else
            {
                current.Add(message);
            }
        }

        return turns;
    }
}
