using System.Text;
using BC_LocalAI.Models.ChatHistory;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>Estimates how many model tokens a text uses.</summary>
public interface ITokenEstimator
{
    int Estimate(string text);
}

/// <summary>
/// Heuristic estimate without a tokenizer: one token per 3 UTF-8 bytes plus a per-message overhead.
/// A sample of Thai text does not establish an upper bound for all prompts or chat-template overhead.
/// Not a production token-budget guarantee: an approved AI multi-turn/tokenizer contract is required before wiring this in.
/// </summary>
public sealed class Utf8TokenEstimator : ITokenEstimator
{
    public const int PerMessageOverhead = 4;

    public int Estimate(string text) =>
        PerMessageOverhead + (int)((Encoding.UTF8.GetByteCount(text ?? string.Empty) + 2L) / 3);
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
public sealed class ConversationContextBuilder
{
    private readonly ITokenEstimator _estimator;

    public ConversationContextBuilder(ITokenEstimator estimator) => _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));

    public ConversationContextBuilder() : this(new Utf8TokenEstimator())
    {
    }

    /// <param name="history">Earlier messages of the conversation (any order; sorted by <see cref="ChatMessage.Sequence"/>).</param>
    /// <param name="tokenBudget">Tokens available for the earlier messages (the new question is budgeted separately by the caller).</param>
    public ConversationContext Build(IEnumerable<ChatMessage> history, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);

        var input = history.ToList();
        if (input.Select(m => m.ConversationId).Distinct().Skip(1).Any())
            throw new ArgumentException("Context must belong to one conversation.", nameof(history));
        if (input.Any(m => m.Sequence <= 0 || !Enum.IsDefined(m.Role) || !Enum.IsDefined(m.Status) || m.Content is null) ||
            input.Select(m => m.Sequence).Distinct().Count() != input.Count)
            throw new ArgumentException("History contains invalid message metadata.", nameof(history));

        var completed = input
            .Where(m => m.Status == MessageStatus.Completed)
            .OrderBy(m => m.Sequence)
            .ToList();

        var turns = GroupIntoTurns(completed);
        var kept = new List<IReadOnlyList<ChatMessage>>();
        var used = 0;

        for (var i = turns.Count - 1; i >= 0; i--)
        {
            long cost = 0;
            foreach (var message in turns[i])
            {
                var estimate = _estimator.Estimate(message.Content);
                if (estimate < 0) throw new ArgumentException("Token estimates must not be negative.");
                cost = checked(cost + estimate);
            }
            if (cost > tokenBudget - used)
            {
                break; // older turns are dropped too: the context must stay contiguous
            }

            used += (int)cost; // safe: cost <= remaining int budget
            kept.Add(turns[i]);
        }

        kept.Reverse();
        var messages = kept.SelectMany(t => t).ToList();
        return new ConversationContext(messages, used, completed.Count - messages.Count);
    }

    /// <summary>A turn starts with a user question. Orphan assistant messages are excluded, not attached to another turn.</summary>
    private static List<IReadOnlyList<ChatMessage>> GroupIntoTurns(List<ChatMessage> messages)
    {
        var turns = new List<IReadOnlyList<ChatMessage>>();
        List<ChatMessage>? current = null;

        foreach (var message in messages)
        {
            if (message.Role == MessageRole.User)
            {
                current = [message];
                turns.Add(current);
            }
            else if (current is not null)
            {
                current.Add(message);
            }
        }

        return turns;
    }
}
