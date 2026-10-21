using System.Text.Json;
using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Ollama;

/// <summary>Request body for Ollama POST /api/generate (contract: Issue #5 / AI-002).</summary>
public sealed class OllamaGenerateRequest
{
    // AI-008 follow-up (docs/ai/system-prompt-echo-fix.md): the first sentence alone made qwen3:4b repeat short requests such as
    // "ขอโค้ด ..." back instead of answering, and after a few short turns answer with the previous short answer (16 of 20 bad).
    // The rule plus three examples gave 0 bad answers in 60 requests (no history, 2 and 3 earlier turns); the rule alone 3 of 60.
    public const string SystemPrompt =
        "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer. " +
        "Always answer the request itself: never repeat or restate the user's message instead of answering it. " +
        "A short request such as 'ขอโค้ด ...' or 'ขอสูตร ...' means: give that code or recipe. " +
        "Examples of the expected behaviour: " +
        "user: 'ขอสูตรไข่เจียว' -> {\"answer\": \"ไข่เจียว: ตีไข่ 2 ฟอง ใส่น้ำปลา 1 ช้อนชา ทอดน้ำมันร้อนจนสุกเหลือง\"}; " +
        "user: 'ขอโค้ด python พิมพ์เลข 1 ถึง 3' -> {\"answer\": \"for i in range(1, 4): print(i)\"}; " +
        "user: 'ขอวิธีล้างแก้ว' -> {\"answer\": \"ใช้น้ำยาล้างจานกับฟองน้ำ ถูให้ทั่ว แล้วล้างด้วยน้ำสะอาด\"}.";

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
    [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
}
