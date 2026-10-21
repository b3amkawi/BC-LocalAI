using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.ChatHistory;

// API shapes for FE-003 (docs/chat-history/BE-007-API-CONTRACT.md). Never the persistence records: no owner id, no sequence counter.

public sealed record ConversationDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt)
{
    public static ConversationDto From(Conversation conversation) =>
        new(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt);
}

public sealed record MessageDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt)
{
    public static MessageDto From(ChatMessage message) => new(
        message.Id, message.ConversationId, message.Sequence, message.Role == MessageRole.User ? "user" : "assistant",
        message.Content, message.Status.ToString().ToLowerInvariant(), message.ErrorCode, message.CreatedAt, message.UpdatedAt);
}

public sealed record PageDto<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed class ConversationTitleRequest
{
    [JsonPropertyName("title")] public string? Title { get; init; }
}
