using System.Text.Json.Serialization;

namespace BC_LocalAI.Models;

public sealed record ChatResponse(string Response, string Model)
{
    /// <summary>BE-007: set only when the turn was saved in a conversation; absent otherwise (unchanged response).</summary>
    [JsonPropertyName("conversationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ConversationId { get; init; }

    /// <summary>BE-007: id of the saved answer message.</summary>
    [JsonPropertyName("messageId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MessageId { get; init; }
}
