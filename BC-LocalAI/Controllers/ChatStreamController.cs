using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Models.Streaming;
using BC_LocalAI.Services;
using BC_LocalAI.Services.ChatAdmission;
using BC_LocalAI.Configuration;
using BC_LocalAI.Security;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/chat/stream")]
[Authorize(Policy = BcPolicies.ChatUser)] // checked before any SSE header is written
[FrontendHistoryMutation]
public sealed class ChatStreamController(
    IStreamingAIService streamingService,
    StreamSessionGuard sessionGuard,
    ChatTurnService turns,
    IOptions<ChatHistoryOptions> historyOptions,
    TimeProvider clock,
    IOptions<OllamaOptions> options,
    ChatAdmissionQueue admission,
    ILogger<ChatStreamController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
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

        // BE-008: a full queue (503) or the user's own quota (429) is answered here as a plain HTTP error, before the question is
        // saved and before any SSE header is written; ExceptionHandlingMiddleware turns the exception into the JSON error.
        using var ticket = admission.Admit(ChatAdmissionQueue.UserKey(User));

        // BE-007: a turn inside a conversation is set up before any SSE header, so refusals are plain JSON errors.
        ChatTurn? turn = null;
        if (request.ConversationId is not null)
        {
            (int Status, string Code, string Message)? refused = null;
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                turn = turns.Enabled && userId is not null
                    ? await turns.BeginAsync(userId, ConversationsController.Id(request.ConversationId), request.Message, cancellationToken)
                    : null;
                if (turn is null)
                    refused = (StatusCodes.Status404NotFound, ConversationsController.NotFoundCode, ConversationsController.NotFoundMessage);
            }
            catch (ChatHistoryValidationException exception)
            {
                refused = (StatusCodes.Status400BadRequest, exception.Code, exception.Message);
            }
            catch (ChatHistoryUnavailableException)
            {
                refused = (StatusCodes.Status503ServiceUnavailable, ChatHistoryErrorsAttribute.HistoryUnavailableCode,
                    ChatHistoryErrorsAttribute.HistoryUnavailableMessage);
            }
            catch (ChatTurnConflictException)
            {
                refused = (StatusCodes.Status409Conflict, ChatHistoryErrorsAttribute.HistoryConflictCode,
                    ChatHistoryErrorsAttribute.HistoryConflictMessage);
            }

            if (refused is { } error)
            {
                Response.StatusCode = error.Status;
                Response.Headers.CacheControl = "no-store";
                await Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(error.Code, error.Message)), cancellationToken);
                return;
            }
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var partial = false;
        var answer = new StringBuilder();
        var checkpointEvery = TimeSpan.FromSeconds(historyOptions.Value.StreamCheckpointSeconds);
        var lastCheckpoint = clock.GetUtcNow();
        var started = new StreamStartedEvent(options.Value.Model) { ConversationId = turn?.ConversationId, MessageId = turn?.AnswerId };
        if (!await TryWriteEventAsync("started", started, cancellationToken))
        {
            if (turn is not null) await turns.CancelAsync(turn, "");
            return;
        }

        string terminalEvent;
        object terminalData;
        try
        {
            // Admitted but no slot free yet: wait in line. "started" only means the request was accepted, not that the model runs.
            var emitQueueEvents = admission.Options.EmitQueueEvents;
            if (ticket.Queued && emitQueueEvents
                && !await TryWriteEventAsync("queued", new StreamQueuedEvent(ticket.Ahead), cancellationToken))
            {
                throw new DownstreamClosedException();
            }

            await ticket.WaitAsync(cancellationToken);
            if (ticket.Queued && emitQueueEvents
                && !await TryWriteEventAsync("processing", new StreamProcessingEvent(), cancellationToken))
            {
                throw new DownstreamClosedException();
            }

            var completion = await streamingService.StreamResponseAsync(
                turn?.Prompt ?? request.Message,
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
                    answer.Append(delta);

                    // Partial text is saved every few seconds, so an interrupted answer keeps most of what the user saw.
                    if (turn is not null && clock.GetUtcNow() - lastCheckpoint >= checkpointEvery)
                    {
                        await Checkpoint(turn, answer.ToString(), token);
                        lastCheckpoint = clock.GetUtcNow();
                    }
                },
                cancellationToken);

            if (turn is null)
            {
                terminalEvent = "completed";
                terminalData = new StreamCompletedEvent(completion.FinishReason, completion.EvalCount);
            }
            else
            {
                // "completed" only after the full answer is stored: the event itself is the proof that the history has it.
                (terminalEvent, terminalData) = await Complete(turn, answer.ToString(), completion);
            }
        }
        catch (AiServiceException exception)
        {
            if (turn is not null) await turns.FailAsync(turn, answer.ToString(), exception.Code);
            terminalEvent = "error";
            terminalData = new StreamErrorEvent(exception.Code, exception.Message, partial)
            {
                RetryAfterSeconds = exception.RetryAfterSeconds
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (turn is not null) await turns.CancelAsync(turn, answer.ToString());
            // Best effort only: a disconnected browser cannot receive an event, but a still-writable caller can.
            using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await TryWriteEventAsync("cancelled", new { }, delivery.Token);
            return;
        }
        catch (DownstreamClosedException)
        {
            if (turn is not null) await turns.CancelAsync(turn, answer.ToString());
            return;
        }
        catch (SessionEndedException)
        {
            // Throwing out of the callback already stopped reading from Ollama and closed that connection.
            if (turn is not null) await turns.FailAsync(turn, answer.ToString(), SessionEndedCode);
            terminalEvent = "error";
            terminalData = new StreamErrorEvent(SessionEndedCode, SessionEndedMessage, partial);
        }
        catch (HistoryWriteException exception)
        {
            if (turn is not null) await turns.FailAsync(turn, answer.ToString(), exception.Code);
            terminalEvent = "error";
            terminalData = new StreamErrorEvent(exception.Code, exception.Message, partial);
        }
        catch (Exception exception)
        {
            logger.LogError("An unexpected streaming error occurred. FailureType={FailureType}", exception.GetType().Name);
            if (turn is not null) await turns.FailAsync(turn, answer.ToString(), "INTERNAL_ERROR");
            terminalEvent = "error";
            terminalData = new StreamErrorEvent("INTERNAL_ERROR", "An unexpected error occurred", partial);
        }

        // The slot and the user's quota are free before the last event goes out, so the next question sent on seeing it is not refused.
        ticket.Dispose();

        // A terminal write failure must never cause a second terminal event on the same stream.
        await TryWriteEventAsync(terminalEvent, terminalData, cancellationToken);
    }

    private async Task Checkpoint(ChatTurn turn, string text, CancellationToken token)
    {
        try
        {
            await turns.CheckpointAsync(turn, text, token);
        }
        catch (ChatHistoryUnavailableException)
        {
            throw new HistoryWriteException(ChatHistoryErrorsAttribute.HistoryUnavailableCode, ChatHistoryErrorsAttribute.HistoryUnavailableMessage);
        }
        catch (ChatTurnConflictException)
        {
            throw new HistoryWriteException(ConversationsController.NotFoundCode, ConversationsController.NotFoundMessage);
        }
    }

    private async Task<(string Event, object Data)> Complete(ChatTurn turn, string text, AiStreamCompletion completion)
    {
        try
        {
            return await turns.CompleteAsync(turn, text)
                ? ("completed", new StreamCompletedEvent(completion.FinishReason, completion.EvalCount))
                : ("error", new StreamErrorEvent(ConversationsController.NotFoundCode, ConversationsController.NotFoundMessage, true));
        }
        catch (ChatHistoryUnavailableException)
        {
            return ("error", new StreamErrorEvent(ChatHistoryErrorsAttribute.HistoryUnavailableCode,
                ChatHistoryErrorsAttribute.HistoryUnavailableMessage, true));
        }
        catch (ChatTurnConflictException)
        {
            return ("error", new StreamErrorEvent(ChatHistoryErrorsAttribute.HistoryConflictCode,
                ChatHistoryErrorsAttribute.HistoryConflictMessage, true));
        }
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

    private sealed class HistoryWriteException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
