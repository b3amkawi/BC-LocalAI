using BC_LocalAI.Models;
using BC_LocalAI.Services;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = BcPolicies.ChatUser)]
public sealed class ChatController(IAIService aiService, StreamSessionGuard sessionGuard) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
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

        var response = await aiService.GenerateResponseAsync(
            request.Message,
            cancellationToken);

        // The answer can take up to the Ollama deadline: do not hand it to a session that ended meanwhile.
        if (!await sessionGuard.IsStillValidAsync(User, cancellationToken))
        {
            return Unauthorized(new ApiErrorResponse(new ApiError(
                ChatStreamController.SessionEndedCode, ChatStreamController.SessionEndedMessage)));
        }

        return Ok(response);
    }
}
