using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Ollama;

internal sealed class OllamaStreamRecord
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("response")] public string? Response { get; init; }
    [JsonPropertyName("thinking")] public string? Thinking { get; init; }
    [JsonPropertyName("done")] public bool? Done { get; init; }
    [JsonPropertyName("done_reason")] public string? DoneReason { get; init; }
    [JsonPropertyName("eval_count")] public int? EvalCount { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
