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

    /// <summary>BE-008: seconds for the Retry-After header (or the stream error event) when the refusal is about load; null otherwise.</summary>
    public int? RetryAfterSeconds { get; private init; }

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

    /// <summary>BE-008: the system is full (queue full, or the wait for a slot ran out). Distinct from AI_TIMEOUT, which is slow generation.</summary>
    public static AiServiceException Busy(int retryAfterSeconds, string reason) =>
        new("AI_BUSY", "ตอนนี้มีผู้ใช้จำนวนมาก กรุณาลองใหม่ในอีกสักครู่", HttpStatusCode.ServiceUnavailable, null, reason)
        { RetryAfterSeconds = retryAfterSeconds };

    /// <summary>BE-008: the signed-in user already has the allowed number of requests in flight.</summary>
    public static AiServiceException RequestInProgress(int retryAfterSeconds) =>
        new("AI_REQUEST_IN_PROGRESS", "คุณมีคำถามที่กำลังตอบอยู่ กรุณารอให้เสร็จก่อนถามคำถามถัดไป", HttpStatusCode.TooManyRequests, null, "user-quota")
        { RetryAfterSeconds = retryAfterSeconds };
}
