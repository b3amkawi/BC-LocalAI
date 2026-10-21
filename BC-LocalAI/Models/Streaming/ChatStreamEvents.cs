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
    [property: JsonPropertyName("partial")] bool Partial);
