using System.Text.Json;
using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Ollama;

/// <summary>Request body for Ollama POST /api/generate (contract: Issue #5 / AI-002).</summary>
public sealed class OllamaGenerateRequest
{
    public const string SystemPrompt =
        "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.";

    private static readonly JsonElement AnswerSchema = JsonDocument.Parse(
        """
        {"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}
        """).RootElement.Clone();

    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("stream")] public bool Stream { get; init; }
    [JsonPropertyName("think")] public bool Think { get; init; }
    [JsonPropertyName("system")] public string System { get; init; } = SystemPrompt;
    [JsonPropertyName("format")] public JsonElement Format { get; init; } = AnswerSchema;
    [JsonPropertyName("options")] public OllamaGenerateOptions Options { get; init; } = new();
}

public sealed class OllamaGenerateOptions
{
    [JsonPropertyName("temperature")] public int Temperature { get; init; }
    [JsonPropertyName("num_predict")] public int NumPredict { get; init; } = 256;
}
