using Microsoft.Extensions.Options;

namespace BC_LocalAI.Configuration;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3:4b";
    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public const int MaxTimeoutSeconds = 600;

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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
