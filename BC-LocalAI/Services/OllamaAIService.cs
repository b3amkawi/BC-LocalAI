using System.Buffers;
using System.Diagnostics;
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
        // A caller that is already gone must not cost Ollama any work.
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var request = new OllamaGenerateRequest
        {
            Model = _options.Model,
            Prompt = message,
            Options = new OllamaGenerateOptions { NumPredict = _options.MaxOutputTokens }
        };

        // One deadline for the whole exchange: connecting, waiting in Ollama's queue, generating and reading
        // the body. It is separate from the caller's token, so a timeout and a client cancellation can be told apart.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GeneratePath)
            {
                Content = JsonContent.Create(request, options: RequestJson)
            };

            // Headers only: the body is read below with a size limit and under the same deadline.
            // Disposing the response closes the connection, which makes Ollama stop generating.
            using var response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                OllamaLog.ProviderError(logger, (int)response.StatusCode, ElapsedMs(started));
                throw AiServiceException.ProviderError();
            }

            var body = await ReadBoundedAsync(response.Content, started, deadline.Token);
            var result = ParseAnswer(body, started);

            // A caller that has already gone away must never get a success.
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client disconnected or the host is stopping: not an AI failure.
            OllamaLog.CancelledByCaller(logger, ElapsedMs(started));
            throw;
        }
        catch (OperationCanceledException)
        {
            // The deadline (or HttpClient's own timeout) elapsed while the caller was still waiting.
            OllamaLog.TimedOut(logger, _options.TimeoutSeconds, ElapsedMs(started));
            throw AiServiceException.Timeout();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            OllamaLog.Unavailable(logger, exception, _options.BaseUrl, ElapsedMs(started));
            throw AiServiceException.Unavailable(exception);
        }
    }

    /// <summary>Reads the whole body but never more than <see cref="OllamaOptions.MaxResponseBytes"/>.</summary>
    private async Task<byte[]> ReadBoundedAsync(HttpContent content, long started, CancellationToken cancellationToken)
    {
        var limit = _options.MaxResponseBytes;
        var declared = content.Headers.ContentLength;

        if (declared > limit)
        {
            LogBodyTooLarge(declared.Value, started);
            throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.BodySizeLimit);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var collected = new MemoryStream(capacity: (int)Math.Min(declared ?? 16 * 1024, limit));
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (collected.Length + read > limit)
                {
                    LogBodyTooLarge(collected.Length + read, started);
                    throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.BodySizeLimit);
                }

                collected.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return collected.ToArray();
    }

    private void LogBodyTooLarge(long bodyBytes, long started) =>
        OllamaLog.ResponseTooLarge(
            logger,
            ResponseTooLargeReasons.BodySizeLimit,
            evalTokens: null,
            _options.MaxOutputTokens,
            bodyBytes,
            _options.MaxResponseBytes,
            ElapsedMs(started));

    // Success conditions are defined in the Ollama Integration Contract (Issue #5 / AI-002).
    // BE-003: an answer cut off by the output token limit is "too large", not "malformed".
    private ChatResponse ParseAnswer(byte[] bodyBytes, long started)
    {
        OllamaGenerateResponse? body;
        try
        {
            body = JsonSerializer.Deserialize<OllamaGenerateResponse>(bodyBytes);
        }
        catch (JsonException exception)
        {
            throw Invalid(InvalidResponseReasons.BodyNotJson, bodyBytes.Length, started, exception);
        }

        if (body is null)
        {
            throw Invalid(InvalidResponseReasons.EmptyBody, bodyBytes.Length, started);
        }

        if (body.Model != _options.Model)
        {
            throw Invalid(InvalidResponseReasons.ModelMismatch, bodyBytes.Length, started);
        }

        if (body.Done is not true)
        {
            throw Invalid(InvalidResponseReasons.NotDone, bodyBytes.Length, started);
        }

        if (body.DoneReason == "length")
        {
            OllamaLog.ResponseTooLarge(
                logger,
                ResponseTooLargeReasons.OutputTokenLimit,
                body.EvalCount,
                _options.MaxOutputTokens,
                bodyBytes.Length,
                _options.MaxResponseBytes,
                ElapsedMs(started));
            throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.OutputTokenLimit);
        }

        if (body.DoneReason != "stop")
        {
            throw Invalid(InvalidResponseReasons.UnexpectedDoneReason, bodyBytes.Length, started);
        }

        var answer = ExtractAnswer(body.Response, bodyBytes.Length, started);
        OllamaLog.Answered(
            logger,
            _options.Model,
            ElapsedMs(started),
            body.EvalCount,
            bodyBytes.Length,
            answer.Length,
            _options.MaxOutputTokens);
        return new ChatResponse(answer, _options.Model);
    }

    private string ExtractAnswer(string? payload, int bodyBytes, long started)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw Invalid(InvalidResponseReasons.EmptyAnswerPayload, bodyBytes, started);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw Invalid(InvalidResponseReasons.AnswerNotJson, bodyBytes, started, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(InvalidResponseReasons.AnswerNotObject, bodyBytes, started);
            }

            var properties = root.EnumerateObject().ToList();
            if (properties.Count != 1
                || properties[0].Name != "answer"
                || properties[0].Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid(InvalidResponseReasons.AnswerFieldInvalid, bodyBytes, started);
            }

            var answer = properties[0].Value.GetString();
            if (string.IsNullOrWhiteSpace(answer))
            {
                throw Invalid(InvalidResponseReasons.AnswerEmpty, bodyBytes, started);
            }

            if (answer.Contains("<think>", StringComparison.OrdinalIgnoreCase)
                || answer.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(InvalidResponseReasons.ThinkMarker, bodyBytes, started);
            }

            return answer;
        }
    }

    private AiServiceException Invalid(string reason, int bodyBytes, long started, Exception? inner = null)
    {
        OllamaLog.InvalidResponse(logger, reason, bodyBytes, ElapsedMs(started));
        return AiServiceException.InvalidResponse(reason, inner);
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

/// <summary>Bounded categories for AI_INVALID_RESPONSE, used in logs and tests (never sent to callers).</summary>
public static class InvalidResponseReasons
{
    public const string BodyNotJson = "BodyNotJson";
    public const string EmptyBody = "EmptyBody";
    public const string ModelMismatch = "ModelMismatch";
    public const string NotDone = "NotDone";
    public const string UnexpectedDoneReason = "UnexpectedDoneReason";
    public const string EmptyAnswerPayload = "EmptyAnswerPayload";
    public const string AnswerNotJson = "AnswerNotJson";
    public const string AnswerNotObject = "AnswerNotObject";
    public const string AnswerFieldInvalid = "AnswerFieldInvalid";
    public const string AnswerEmpty = "AnswerEmpty";
    public const string ThinkMarker = "ThinkMarker";
}

/// <summary>Bounded categories for AI_RESPONSE_TOO_LARGE.</summary>
public static class ResponseTooLargeReasons
{
    /// <summary>Ollama stopped at the output token limit (done_reason = length).</summary>
    public const string OutputTokenLimit = "OutputTokenLimit";

    /// <summary>The response body is larger than Ollama:MaxResponseBytes.</summary>
    public const string BodySizeLimit = "BodySizeLimit";
}
