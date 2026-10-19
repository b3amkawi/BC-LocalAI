using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models;
using BC_LocalAI.Models.Ollama;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services;

public sealed class OllamaAIService(
    HttpClient httpClient,
    IOptions<OllamaOptions> options,
    ILogger<OllamaAIService> logger) : IAIService
{
    private const string GeneratePath = "api/generate";

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        // Keep Thai text readable in the request body (UTF-8, not \uXXXX).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly OllamaOptions _options = options.Value;

    public async Task<ChatResponse> GenerateResponseAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var request = new OllamaGenerateRequest { Model = _options.Model, Prompt = message };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(GeneratePath, request, RequestJson, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (the caller did not cancel).
            logger.LogWarning("Ollama request timed out after {TimeoutSeconds}s.", _options.TimeoutSeconds);
            throw AiServiceException.Timeout();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Ollama is unavailable at {BaseUrl}.", _options.BaseUrl);
            throw AiServiceException.Unavailable(exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw AiServiceException.ProviderError();
            }

            OllamaGenerateResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw AiServiceException.Timeout();
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                logger.LogWarning("Ollama returned a malformed response body.");
                throw AiServiceException.InvalidResponse(exception);
            }

            var answer = ExtractAnswer(body);
            return new ChatResponse(answer, _options.Model);
        }
    }

    // Success conditions are defined in the Ollama Integration Contract (Issue #5 / AI-002).
    private string ExtractAnswer(OllamaGenerateResponse? body)
    {
        if (body is null
            || body.Model != _options.Model
            || body.Done is not true
            || body.DoneReason != "stop"
            || string.IsNullOrWhiteSpace(body.Response))
        {
            logger.LogWarning("Ollama response did not meet the success conditions.");
            throw AiServiceException.InvalidResponse();
        }

        try
        {
            using var document = JsonDocument.Parse(body.Response);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw AiServiceException.InvalidResponse();
            }

            var properties = root.EnumerateObject().ToList();
            if (properties.Count != 1
                || properties[0].Name != "answer"
                || properties[0].Value.ValueKind != JsonValueKind.String)
            {
                throw AiServiceException.InvalidResponse();
            }

            var answer = properties[0].Value.GetString();
            if (string.IsNullOrWhiteSpace(answer)
                || answer.Contains("<think>", StringComparison.OrdinalIgnoreCase)
                || answer.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            {
                throw AiServiceException.InvalidResponse();
            }

            return answer;
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Ollama answer payload is not valid JSON.");
            throw AiServiceException.InvalidResponse(exception);
        }
    }
}
