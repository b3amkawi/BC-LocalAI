using Microsoft.Extensions.Options;

namespace BC_LocalAI.Configuration;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3:4b";
    /// <summary>Deadline for the whole exchange with Ollama (queue wait, generation and reading the answer).</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Output token limit sent to Ollama (num_predict). An answer that needs more is cut off by Ollama and reported as
    /// AI_RESPONSE_TOO_LARGE. 1024 leaves about 4x headroom over the longest answers measured (247 tokens); see docs/BE-003-CONFIGURATION.md.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Largest response body accepted from Ollama. Protects memory from a misbehaving provider.</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;
}

public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public const int MaxTimeoutSeconds = 600;
    public const int MinOutputTokens = 16;
    public const int MaxOutputTokensLimit = 4096; // the model context window (num_ctx) is 4096 by default
    public const int MinResponseBytes = 4 * 1024;
    public const int MaxResponseBytesLimit = 16 * 1024 * 1024;

    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        var failures = new List<string>();

        // AI-002 contract: Ollama is reached through loopback only, never over a network.
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsLoopback)
        {
            failures.Add("Ollama:BaseUrl must be an absolute loopback http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add("Ollama:Model is required.");
        }

        if (options.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"Ollama:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");
        }

        if (options.MaxOutputTokens is < MinOutputTokens or > MaxOutputTokensLimit)
        {
            failures.Add($"Ollama:MaxOutputTokens must be between {MinOutputTokens} and {MaxOutputTokensLimit}.");
        }

        if (options.MaxResponseBytes is < MinResponseBytes or > MaxResponseBytesLimit)
        {
            failures.Add($"Ollama:MaxResponseBytes must be between {MinResponseBytes} and {MaxResponseBytesLimit}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
