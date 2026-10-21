namespace BC_LocalAI.Services.ChatAdmission;

/// <summary>BE-008 structured logs. Values are bounded categories and numbers only: never a question, an answer, a user id or a token.</summary>
internal static partial class ChatQueueLog
{
    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning,
        Message = "Chat request refused at admission. Reason={Reason} Active={Active} Queued={Queued}")]
    public static partial void Rejected(ILogger logger, string reason, int active, int queued);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning,
        Message = "Chat request waited a long time for a slot. WaitedMs={WaitedMs}")]
    public static partial void LongWait(ILogger logger, long waitedMs);
}
