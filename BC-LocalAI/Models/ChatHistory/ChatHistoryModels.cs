namespace BC_LocalAI.Models.ChatHistory;

/// <summary>Who wrote a message. Stored as a short string in the database (see docs/chat-history/BE-007-DESIGN.md).</summary>
public enum MessageRole
{
    User,
    Assistant
}

/// <summary>
/// Life cycle of a message. Only <see cref="Completed"/> is a finished answer; every other state is either still running or
/// ended without a complete answer and must never be shown or reused as one (BE-007: "do not save an incomplete response as completed").
/// </summary>
public enum MessageStatus
{
    /// <summary>Created, the AI has not produced anything yet.</summary>
    Pending,
    /// <summary>Streaming: partial content is being received (BE-004).</summary>
    Streaming,
    Completed,
    /// <summary>Ended with an error; <see cref="ChatMessage.ErrorCode"/> holds the API error code.</summary>
    Failed,
    /// <summary>The user stopped the answer or left.</summary>
    Cancelled
}

/// <summary>A conversation owned by exactly one user.</summary>
public sealed record Conversation(
    Guid Id,
    string UserId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One message of a conversation. <see cref="Sequence"/> gives the order inside the conversation.</summary>
public sealed record ChatMessage(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    MessageRole Role,
    string Content,
    MessageStatus Status,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Page request. <paramref name="Cursor"/> is opaque: pass back the value from the previous page.</summary>
public sealed record PageRequest(int Limit = PageRequest.DefaultLimit, string? Cursor = null)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
}

/// <summary>One page of results. <paramref name="NextCursor"/> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
