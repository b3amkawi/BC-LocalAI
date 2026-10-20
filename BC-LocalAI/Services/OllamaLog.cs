namespace BC_LocalAI.Services;

/// <summary>
/// Structured log messages for the Ollama call. Every value is a number, a bounded category or the configured
/// model name: the user's question and the model's answer are never logged.
/// </summary>
internal static partial class OllamaLog
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Ollama answered. Model={Model} ElapsedMs={ElapsedMs} EvalTokens={EvalTokens} BodyBytes={BodyBytes} AnswerChars={AnswerChars} MaxOutputTokens={MaxOutputTokens}")]
    public static partial void Answered(
        ILogger logger, string model, long elapsedMs, int? evalTokens, int bodyBytes, int answerChars, int maxOutputTokens);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "Ollama did not answer in time. TimeoutSeconds={TimeoutSeconds} ElapsedMs={ElapsedMs}")]
    public static partial void TimedOut(ILogger logger, int timeoutSeconds, long elapsedMs);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Information,
        Message = "Request cancelled by the caller; the Ollama call was stopped. ElapsedMs={ElapsedMs}")]
    public static partial void CancelledByCaller(ILogger logger, long elapsedMs);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "Ollama is unavailable. Endpoint={Endpoint} ElapsedMs={ElapsedMs}")]
    public static partial void Unavailable(ILogger logger, Exception exception, string endpoint, long elapsedMs);

    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Warning,
        Message = "Ollama returned an HTTP error. StatusCode={StatusCode} ElapsedMs={ElapsedMs}")]
    public static partial void ProviderError(ILogger logger, int statusCode, long elapsedMs);

    [LoggerMessage(
        EventId = 4006,
        Level = LogLevel.Warning,
        Message = "Ollama response is invalid. Reason={Reason} BodyBytes={BodyBytes} ElapsedMs={ElapsedMs}")]
    public static partial void InvalidResponse(ILogger logger, string reason, int bodyBytes, long elapsedMs);

    [LoggerMessage(
        EventId = 4007,
        Level = LogLevel.Warning,
        Message = "Ollama response is too large. Reason={Reason} EvalTokens={EvalTokens} MaxOutputTokens={MaxOutputTokens} BodyBytes={BodyBytes} MaxResponseBytes={MaxResponseBytes} ElapsedMs={ElapsedMs}")]
    public static partial void ResponseTooLarge(
        ILogger logger, string reason, int? evalTokens, int maxOutputTokens, long bodyBytes, int maxResponseBytes, long elapsedMs);

    [LoggerMessage(
        EventId = 4008,
        Level = LogLevel.Information,
        Message = "Ollama stream completed. Model={Model} ElapsedMs={ElapsedMs} EvalTokens={EvalTokens} BodyBytes={BodyBytes}")]
    public static partial void StreamAnswered(
        ILogger logger, string model, long elapsedMs, int? evalTokens, int bodyBytes);
}
