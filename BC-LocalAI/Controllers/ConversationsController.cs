using System.Globalization;
using System.Security.Claims;
using BC_LocalAI.Models;
using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Security;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BC_LocalAI.Controllers;

/// <summary>
/// BE-007 conversation management for FE-003. Owner = the signed-in user's id from the session (BE-006), never from the request.
/// Not found, deleted and someone else's conversation all answer the same 404, so ids cannot be probed (IDOR).
/// Only reachable while ChatHistory:Enabled (otherwise 404 before authentication, see ChatHistoryRegistration).
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize(Policy = BcPolicies.ChatUser)]
[ChatHistoryErrors]
public sealed class ConversationsController(IChatHistoryRepository history, ChatTurnService turns) : ControllerBase
{
    public const string NotFoundCode = "CONVERSATION_NOT_FOUND";
    public const string NotFoundMessage = "ไม่พบบทสนทนา";

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ConversationTitleRequest? request, CancellationToken cancellationToken)
    {
        var created = await history.CreateConversationAsync(UserId, request?.Title ?? "", cancellationToken);
        return Created($"/api/conversations/{created.Id}", ConversationDto.From(created));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? limit, [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        var page = await history.ListConversationsAsync(UserId, Page(limit, cursor), cancellationToken);
        return Ok(new PageDto<ConversationDto>(page.Items.Select(ConversationDto.From).ToList(), page.NextCursor));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        await history.GetConversationAsync(UserId, Id(id), cancellationToken) is { } conversation
            ? Ok(ConversationDto.From(conversation))
            : ConversationNotFound();

    [HttpPatch("{id}")]
    public async Task<IActionResult> Rename(string id, [FromBody] ConversationTitleRequest? request, CancellationToken cancellationToken)
    {
        if (request?.Title is null)
            throw new ChatHistoryValidationException("INVALID_TITLE", "Title is required.");
        return await history.RenameConversationAsync(UserId, Id(id), request.Title, cancellationToken) is { } renamed
            ? Ok(ConversationDto.From(renamed))
            : ConversationNotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken) =>
        await history.DeleteConversationAsync(UserId, Id(id), cancellationToken) ? NoContent() : ConversationNotFound();

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> Messages(string id, [FromQuery] string? limit, [FromQuery] string? cursor,
        CancellationToken cancellationToken) =>
        await history.ListMessagesAsync(UserId, Id(id), Page(limit, cursor), cancellationToken) is { } page
            ? Ok(new PageDto<MessageDto>(page.Items.Select(m => MessageDto.From(turns.Present(m))).ToList(), page.NextCursor))
            : ConversationNotFound();

    // The authorization policy guarantees a signed-in user; a missing id is a server fault, never "anonymous history".
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated principal without a user id.");

    internal static NotFoundObjectResult ConversationNotFound() =>
        new NotFoundObjectResult(new ApiErrorResponse(new ApiError(NotFoundCode, NotFoundMessage)));

    internal static Guid Id(string? value) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw new ChatHistoryValidationException("INVALID_ID", "The id is not valid.");

    private static PageRequest Page(string? limit, string? cursor)
    {
        var size = PageRequest.DefaultLimit;
        if (limit is not null && !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out size))
            throw new ChatHistoryValidationException("INVALID_PAGE", $"Limit must be between 1 and {PageRequest.MaxLimit}.");
        return new PageRequest(size, cursor);
    }
}

/// <summary>
/// Maps chat history errors to the API error format: rule violations 400 with their code, storage failure 503
/// HISTORY_UNAVAILABLE (never a silent fallback), all with no-store. Database details never reach the client.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ChatHistoryErrorsAttribute : Attribute, IExceptionFilter, IResultFilter
{
    public const string HistoryUnavailableCode = "HISTORY_UNAVAILABLE";
    public const string HistoryUnavailableMessage = "ระบบประวัติการสนทนาไม่พร้อมใช้งาน";
    public const string HistoryConflictCode = "HISTORY_CONFLICT";
    public const string HistoryConflictMessage = "คำตอบก่อนหน้าในบทสนทนานี้ยังไม่เสร็จ";

    public void OnException(ExceptionContext context)
    {
        context.Result = context.Exception switch
        {
            ChatHistoryValidationException validation => new BadRequestObjectResult(
                new ApiErrorResponse(new ApiError(validation.Code, validation.Message))),
            ChatHistoryUnavailableException => new ObjectResult(new ApiErrorResponse(
                new ApiError(HistoryUnavailableCode, HistoryUnavailableMessage))) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            ChatTurnConflictException => new ConflictObjectResult(new ApiErrorResponse(
                new ApiError(HistoryConflictCode, HistoryConflictMessage))),
            _ => context.Result
        };
        context.ExceptionHandled = context.Result is not null;
        if (context.ExceptionHandled) context.HttpContext.Response.Headers.CacheControl = "no-store";
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (!context.HttpContext.Response.HasStarted) context.HttpContext.Response.Headers.CacheControl = "no-store";
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
