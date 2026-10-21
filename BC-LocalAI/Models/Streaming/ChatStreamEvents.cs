using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Streaming;

public sealed record StreamStartedEvent([property: JsonPropertyName("model")] string Model)
{
    /// <summary>BE-007: present only when the turn is saved in a conversation.</summary>
    [JsonPropertyName("conversationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ConversationId { get; init; }

    /// <summary>BE-007: id of the answer message being saved.</summary>
    [JsonPropertyName("messageId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MessageId { get; init; }
}

public sealed record StreamDeltaEvent([property: JsonPropertyName("text")] string Text);

public sealed record StreamCompletedEvent(
    [property: JsonPropertyName("finishReason")] string FinishReason,
    [property: JsonPropertyName("evalCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EvalCount);

public sealed record StreamErrorEvent(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("partial")] bool Partial)
{
    /// <summary>BE-008: present only on AI_BUSY, where the HTTP header Retry-After can no longer be sent.</summary>
    [JsonPropertyName("retryAfterSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>BE-008 (proposed, only with ChatQueue:EmitQueueEvents): the request waits for a slot. <c>ahead</c> is the number of waiting requests in front of it when it was admitted; it is a snapshot, not a promise.</summary>
public sealed record StreamQueuedEvent([property: JsonPropertyName("ahead")] int Ahead);

/// <summary>BE-008 (proposed): a slot was granted and the request is now sent to the model.</summary>
public sealed record StreamProcessingEvent;
