namespace BC_LocalAI.Security;

/// <summary>
/// Security audit log (BE-006). Only stable user ids, roles, reasons and counts are written:
/// never a password, password hash, security stamp, session id or cookie value.
/// </summary>
internal static partial class SecurityLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Session created. UserId={UserId} Role={Role}")]
    public static partial void SessionCreated(ILogger logger, string userId, string role);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Session ended by sign-out. UserId={UserId}")]
    public static partial void SessionSignedOut(ILogger logger, string userId);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Session refused. Reason={Reason} UserId={UserId}")]
    public static partial void SessionRejected(ILogger logger, SessionRejectReason reason, string userId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "All sessions of a user revoked. UserId={UserId} Count={Count}")]
    public static partial void SessionsRevokedForUser(ILogger logger, string userId, int count);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Cross-site API request refused. Method={Method} Path={Path}")]
    public static partial void CrossSiteRequestRefused(ILogger logger, string method, string path);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Warning, Message = "Access denied. Path={Path} UserId={UserId}")]
    public static partial void AccessDenied(ILogger logger, string path, string userId);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Error, Message = "User store unavailable while checking a session; request refused. UserId={UserId} ExceptionType={ExceptionType}")]
    public static partial void UserStoreUnavailable(ILogger logger, string userId, string exceptionType);
}
