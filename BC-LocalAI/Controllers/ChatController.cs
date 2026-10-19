using BC_LocalAI.Models;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ChatController(IAIService aiService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
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

        return Ok(response);
    }
}
