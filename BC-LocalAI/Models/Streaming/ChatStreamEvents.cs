using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Streaming;

public sealed record StreamStartedEvent([property: JsonPropertyName("model")] string Model);

public sealed record StreamDeltaEvent([property: JsonPropertyName("text")] string Text);

public sealed record StreamCompletedEvent(
    [property: JsonPropertyName("finishReason")] string FinishReason,
    [property: JsonPropertyName("evalCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EvalCount);

public sealed record StreamErrorEvent(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("partial")] bool Partial);
