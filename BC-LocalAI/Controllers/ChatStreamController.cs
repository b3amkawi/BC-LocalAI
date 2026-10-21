using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Models.Streaming;
using BC_LocalAI.Services;
using BC_LocalAI.Configuration;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/chat/stream")]
[Authorize(Policy = BcPolicies.ChatUser)] // checked before any SSE header is written
public sealed class ChatStreamController(
    IStreamingAIService streamingService,
    StreamSessionGuard sessionGuard,
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
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var partial = false;
        if (!await TryWriteEventAsync("started", new StreamStartedEvent(options.Value.Model), cancellationToken)) return;
        string terminalEvent;
        object terminalData;
        try
        {
            var completion = await streamingService.StreamResponseAsync(
                request.Message,
                async (delta, token) =>
                {
                    // The session is re-checked before each piece: once it ends, nothing more of the answer is sent.
                    if (!await sessionGuard.IsStillValidAsync(User, token))
                    {
                        throw new SessionEndedException();
                    }

                    if (!await TryWriteEventAsync("delta", new StreamDeltaEvent(delta), token))
                    {
                        token.ThrowIfCancellationRequested();
                        throw new DownstreamClosedException();
                    }
                    partial = true;
                },
                cancellationToken);
            terminalEvent = "completed";
            terminalData = new StreamCompletedEvent(completion.FinishReason, completion.EvalCount);
        }
        catch (AiServiceException exception)
        {
            terminalEvent = "error";
            terminalData = new StreamErrorEvent(exception.Code, exception.Message, partial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Best effort only: a disconnected browser cannot receive an event, but a still-writable caller can.
            using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await TryWriteEventAsync("cancelled", new { }, delivery.Token);
            return;
        }
        catch (DownstreamClosedException)
        {
            return;
        }
        catch (SessionEndedException)
        {
            // Throwing out of the callback already stopped reading from Ollama and closed that connection.
            terminalEvent = "error";
            terminalData = new StreamErrorEvent(SessionEndedCode, SessionEndedMessage, partial);
        }
        catch (Exception exception)
        {
            logger.LogError("An unexpected streaming error occurred. FailureType={FailureType}", exception.GetType().Name);
            terminalEvent = "error";
            terminalData = new StreamErrorEvent("INTERNAL_ERROR", "An unexpected error occurred", partial);
        }

        // A terminal write failure must never cause a second terminal event on the same stream.
        await TryWriteEventAsync(terminalEvent, terminalData, cancellationToken);
    }

    private async Task<bool> TryWriteEventAsync<T>(string eventName, T data, CancellationToken cancellationToken)
    {
        try
        {
            await Response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data)}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            HttpContext.Abort();
            return false;
        }
    }

    public const string SessionEndedCode = "AUTH_SESSION_ENDED";
    public const string SessionEndedMessage = "เซสชันสิ้นสุดแล้ว กรุณาเข้าสู่ระบบใหม่";

    private sealed class DownstreamClosedException : Exception;

    private sealed class SessionEndedException : Exception;
}
