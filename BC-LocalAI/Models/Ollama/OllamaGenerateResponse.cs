using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Ollama;

/// <summary>Response body from Ollama POST /api/generate (non-streaming).</summary>
public sealed class OllamaGenerateResponse
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("response")] public string? Response { get; init; }
    [JsonPropertyName("done")] public bool? Done { get; init; }
    [JsonPropertyName("done_reason")] public string? DoneReason { get; init; }
}
