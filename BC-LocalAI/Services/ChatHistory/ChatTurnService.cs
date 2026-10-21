using System.Text;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models.ChatHistory;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>One question and its answer inside a saved conversation.</summary>
/// <param name="Prompt">What is sent to the AI: the question, with earlier completed turns when there are any.</param>
public sealed record ChatTurn(string UserId, Guid ConversationId, Guid QuestionId, Guid AnswerId, string Prompt);

/// <summary>An earlier answer of the same conversation is still being produced, or the answer changed meanwhile. Maps to 409.</summary>
public sealed class ChatTurnConflictException() : Exception("Another answer in this conversation is still running.");

/// <summary>
/// Saves a chat turn (BE-007 D3/D5): the question as completed, the answer as pending, then streaming checkpoints and exactly one
/// final state. Only a full answer becomes completed; an error, a stop or a lost session leaves failed / cancelled with the partial
/// text, so an incomplete answer is never shown or reused as a complete one.
/// </summary>
public sealed class ChatTurnService(
    IChatHistoryRepository history,
    ConversationContextBuilder contextBuilder,
    IOptions<ChatHistoryOptions> options,
    IOptions<OllamaOptions> ollama,
    TimeProvider clock,
    ILogger<ChatTurnService> logger)
{
    public const string InterruptedCode = "INTERRUPTED";

    public bool Enabled => options.Value.Enabled;

    /// <summary>An unfinished assistant answer that has not moved for longer than <see cref="RunningWindow"/>.</summary>
    public bool IsAbandoned(ChatMessage message) =>
        message.Role == MessageRole.Assistant && !ChatHistoryRules.IsFinal(message.Status)
        && clock.GetUtcNow() - message.UpdatedAt >= RunningWindow;

    /// <summary>
    /// How a message is shown: an abandoned answer reads as failed <see cref="InterruptedCode"/> even before it is closed in the
    /// database (that happens at the next question in the conversation). Reading never writes.
    /// </summary>
    public ChatMessage Present(ChatMessage message) =>
        IsAbandoned(message) ? message with { Status = MessageStatus.Failed, ErrorCode = InterruptedCode } : message;

    /// <summary>An unfinished answer older than this is treated as abandoned (a crash or a lost process), not as still running.</summary>
    public TimeSpan RunningWindow => TimeSpan.FromSeconds(ollama.Value.TimeoutSeconds + 60);

    /// <returns>null when the conversation is not found for this user (also: deleted, someone else's).</returns>
    /// <exception cref="ChatTurnConflictException">An answer in this conversation is still running (double submit).</exception>
    public async Task<ChatTurn?> BeginAsync(string userId, Guid conversationId, string question, CancellationToken cancellationToken)
    {
        ChatHistoryRules.EnsureValidMessage(MessageRole.User, question);
        var recent = await history.GetRecentMessagesAsync(userId, conversationId, ChatHistoryRules.MaxRecentMessages, cancellationToken);
        if (recent is null) return null;

        var running = recent.LastOrDefault(m => m.Role == MessageRole.Assistant && !ChatHistoryRules.IsFinal(m.Status));
        if (running is not null && !IsAbandoned(running))
            throw new ChatTurnConflictException();

        // Answers left unfinished by a crash are closed now, so they read as interrupted everywhere (not "still running").
        foreach (var abandoned in recent.Where(IsAbandoned))
            await FinishQuietly(new ChatTurn(userId, conversationId, Guid.Empty, abandoned.Id, ""), abandoned.Content,
                MessageStatus.Failed, InterruptedCode);

        var context = contextBuilder.Build(recent, options.Value.ContextTokenBudget);
        var prompt = ConversationPrompt.Compose(context.Messages, question);

        var added = await history.AddTurnAsync(userId, conversationId, question, cancellationToken);
        return added is null ? null : new ChatTurn(userId, conversationId, added.Question.Id, added.Answer.Id, prompt);
    }

    /// <summary>Partial text of a streaming answer.</summary>
    public async Task CheckpointAsync(ChatTurn turn, string partial, CancellationToken cancellationToken)
    {
        if (await history.UpdateMessageAsync(turn.UserId, turn.AnswerId, partial, MessageStatus.Streaming, null, cancellationToken) is null)
            throw new ChatTurnConflictException(); // the conversation was deleted meanwhile
    }

    /// <summary>The full answer. Runs to the end even if the caller leaves: the answer exists and must not be lost half-way.</summary>
    /// <returns>false when the conversation was deleted while the answer was produced (nothing to save it in).</returns>
    public async Task<bool> CompleteAsync(ChatTurn turn, string answer)
    {
        try
        {
            return await history.UpdateMessageAsync(turn.UserId, turn.AnswerId, answer, MessageStatus.Completed, null, CancellationToken.None)
                is not null;
        }
        catch (ChatHistoryValidationException)
        {
            throw new ChatTurnConflictException(); // already final: a stale writer must not overwrite it
        }
    }

    /// <summary>Ended with an error code. Best effort: the caller is already reporting the original error.</summary>
    public Task FailAsync(ChatTurn turn, string partial, string errorCode) =>
        FinishQuietly(turn, partial, MessageStatus.Failed, errorCode);

    /// <summary>The user stopped the answer or left. Best effort, like <see cref="FailAsync"/>.</summary>
    public Task CancelAsync(ChatTurn turn, string partial) => FinishQuietly(turn, partial, MessageStatus.Cancelled, null);

    private async Task FinishQuietly(ChatTurn turn, string partial, MessageStatus status, string? errorCode)
    {
        try
        {
            await history.UpdateMessageAsync(turn.UserId, turn.AnswerId, partial, status, errorCode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is ChatHistoryUnavailableException or ChatHistoryValidationException or ChatTurnConflictException)
        {
            // The row stays pending/streaming; readers treat it as interrupted (never as a complete answer).
            logger.LogWarning(new EventId(5201, "ChatHistoryFinishFailed"),
                "Could not record the end of an answer. Status={Status} FailureType={FailureType}", status, exception.GetType().Name);
        }
    }
}

/// <summary>
/// The prompt for a question asked inside a conversation (BE-007 D4). The AI-002 request is unchanged (one prompt to
/// /api/generate with the same system prompt and JSON answer format): earlier completed question/answer pairs are written
/// before the question in a fixed, clearly delimited template. With no earlier pair the prompt is the question itself,
/// exactly as before BE-007. The template is for review by the AI Engineer.
/// </summary>
public static class ConversationPrompt
{
    public const string Header = "Earlier conversation (oldest first). Use it only as context for the current question:";
    public const string Open = "<conversation>";
    public const string Close = "</conversation>";
    public const string QuestionLabel = "Current question:";

    public static string Compose(IReadOnlyList<ChatMessage> context, string question)
    {
        var pairs = new List<(string Question, string Answer)>();
        for (var i = 0; i + 1 < context.Count; i++)
        {
            // Only complete pairs: a question whose answer failed or was stopped is not repeated to the model.
            if (context[i].Role == MessageRole.User && context[i + 1].Role == MessageRole.Assistant
                && context[i].Status == MessageStatus.Completed && context[i + 1].Status == MessageStatus.Completed)
            {
                pairs.Add((context[i].Content, context[i + 1].Content));
                i++;
            }
        }

        if (pairs.Count == 0) return question;

        var prompt = new StringBuilder().AppendLine(Header).AppendLine(Open);
        foreach (var (asked, answered) in pairs)
        {
            prompt.Append("User: ").AppendLine(Neutralize(asked));
            prompt.Append("Assistant: ").AppendLine(Neutralize(answered));
        }

        return prompt.AppendLine(Close).AppendLine(QuestionLabel).Append(question).ToString();
    }

    // Stored text cannot close the history block early or open a fake one.
    private static string Neutralize(string text) =>
        text.Replace(Open, "<conversation >", StringComparison.OrdinalIgnoreCase)
            .Replace(Close, "</conversation >", StringComparison.OrdinalIgnoreCase);
}
