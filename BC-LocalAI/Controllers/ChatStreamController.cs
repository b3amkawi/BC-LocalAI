using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Models.Streaming;
using BC_LocalAI.Services;
using BC_LocalAI.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/chat/stream")]
public sealed class ChatStreamController(
    IStreamingAIService streamingService,
    IOptions<OllamaOptions> options,
    ILogger<ChatStreamController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task PostAsync([FromBody] ChatRequest? request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ApiErrorResponse(new ApiError("INVALID_MESSAGE", "Message is required")),
                cancellationToken);
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var partial = false;
        await WriteEventAsync("started", new StreamStartedEvent(options.Value.Model), cancellationToken);
        try
        {
            var completion = await streamingService.StreamResponseAsync(
                request.Message,
                async (delta, token) =>
                {
                    partial = true;
                    await WriteEventAsync("delta", new StreamDeltaEvent(delta), token);
                },
                cancellationToken);
            await WriteEventAsync(
                "completed",
                new StreamCompletedEvent(completion.FinishReason, completion.EvalCount),
                cancellationToken);
        }
        catch (AiServiceException exception)
        {
            await WriteEventAsync(
                "error",
                new StreamErrorEvent(exception.Code, exception.Message, partial),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Usually the client connection is already gone. Do not replace cancellation with an AI error.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "An unexpected streaming error occurred.");
            await WriteEventAsync(
                "error",
                new StreamErrorEvent("INTERNAL_ERROR", "An unexpected error occurred", partial),
                cancellationToken);
        }
    }

    private async Task WriteEventAsync<T>(string eventName, T data, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
