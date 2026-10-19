namespace BC_LocalAI.Models;

public sealed record ApiErrorResponse(ApiError Error);

public sealed record ApiError(string Code, string Message);
