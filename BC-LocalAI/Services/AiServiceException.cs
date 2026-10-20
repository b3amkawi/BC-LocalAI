using System.Net;

namespace BC_LocalAI.Services;

/// <summary>
/// Failure reported by an AI provider. Carries the public error contract (code + status)
/// so no provider detail leaks to API callers.
/// </summary>
public sealed class AiServiceException : Exception
{
    public AiServiceException(
        string code,
        string message,
        HttpStatusCode statusCode,
        Exception? inner = null,
        string? reason = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
        Reason = reason;
    }

    public string Code { get; }
    public HttpStatusCode StatusCode { get; }

    /// <summary>Internal, bounded diagnostic category for logs and tests. Never sent to API callers.</summary>
    public string? Reason { get; }

    public static AiServiceException Unavailable(Exception? inner = null) =>
        new("AI_UNAVAILABLE", "AI service is unavailable", HttpStatusCode.ServiceUnavailable, inner);

    public static AiServiceException Timeout(Exception? inner = null) =>
        new("AI_TIMEOUT", "AI service timed out", HttpStatusCode.GatewayTimeout, inner);

    public static AiServiceException ProviderError(Exception? inner = null) =>
        new("AI_PROVIDER_ERROR", "AI service returned an error", HttpStatusCode.BadGateway, inner);

    public static AiServiceException InvalidResponse(string? reason = null, Exception? inner = null) =>
        new("AI_INVALID_RESPONSE", "AI service returned an invalid response", HttpStatusCode.BadGateway, inner, reason);

    /// <summary>The answer is larger than the configured limits (output tokens or response bytes).</summary>
    public static AiServiceException ResponseTooLarge(string reason) =>
        new("AI_RESPONSE_TOO_LARGE", "AI response was too large", HttpStatusCode.BadGateway, null, reason);
}
