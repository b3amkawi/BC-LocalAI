using System.Security.Claims;
using BC_LocalAI.Models;
using BC_LocalAI.Services;
using BC_LocalAI.Security;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = BcPolicies.ChatUser)]
[ChatHistoryErrors]
[FrontendHistoryMutation]
public sealed class ChatController(IAIService aiService, StreamSessionGuard sessionGuard, ChatTurnService turns) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ChatResponse>> PostAsync(
        [FromBody] ChatRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
        {
            return BadRequest(new ApiErrorResponse(
                new ApiError("INVALID_MESSAGE", "Message is required")));
        }

        // BE-007: inside a conversation the question is saved first and earlier turns become context.
        ChatTurn? turn = null;
        if (request.ConversationId is not null)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            turn = turns.Enabled && userId is not null
                ? await turns.BeginAsync(userId, ConversationsController.Id(request.ConversationId), request.Message, cancellationToken)
                : null;
            if (turn is null) return ConversationsController.ConversationNotFound();
        }

        ChatResponse response;
        try
        {
            response = await aiService.GenerateResponseAsync(turn?.Prompt ?? request.Message, cancellationToken);
        }
        catch (AiServiceException exception) when (turn is not null)
        {
            await turns.FailAsync(turn, "", exception.Code);
            throw;
        }
        catch (OperationCanceledException) when (turn is not null)
        {
            await turns.CancelAsync(turn, "");
            throw;
        }

        // The answer can take up to the Ollama deadline: do not hand it to a session that ended meanwhile.
        if (!await sessionGuard.IsStillValidAsync(User, cancellationToken))
        {
            if (turn is not null) await turns.FailAsync(turn, "", ChatStreamController.SessionEndedCode);
            return Unauthorized(new ApiErrorResponse(new ApiError(
                ChatStreamController.SessionEndedCode, ChatStreamController.SessionEndedMessage)));
        }

        if (turn is null) return Ok(response);

        // Saved before it is handed over; if saving fails the answer is not reported as saved (503 HISTORY_UNAVAILABLE).
        return await turns.CompleteAsync(turn, response.Response)
            ? Ok(response with { ConversationId = turn.ConversationId, MessageId = turn.AnswerId })
            : (ActionResult)ConversationsController.ConversationNotFound(); // deleted by its owner while the answer was produced
    }
}
