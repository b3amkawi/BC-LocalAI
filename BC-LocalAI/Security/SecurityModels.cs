namespace BC_LocalAI.Security;

/// <summary>Roles of BC-LocalAI (SEC-001 decision 2026-10-20: User and Admin only).</summary>
public static class BcRoles
{
    public const string User = "User";
    public const string Admin = "Admin";

    public static bool IsKnown(string? role) => role is User or Admin;
}

/// <summary>Authorization policy names. Endpoints use these, never role strings directly.</summary>
public static class BcPolicies
{
    /// <summary>May use the AI chat (User and Admin). Allows everyone while Authentication:Enabled is false.</summary>
    public const string ChatUser = "ChatUser";

    /// <summary>Account administration only. Admin never gains access to other users' conversations.</summary>
    public const string Admin = "Admin";
}

/// <summary>Claims written into the authentication cookie. Nothing secret: no password, hash or security stamp.</summary>
public static class BcClaimTypes
{
    /// <summary>Server-side session id; the cookie is useless once that session is revoked.</summary>
    public const string SessionId = "bc:sid";
}

/// <summary>A server-side session. Revoking it invalidates the cookie even before the cookie expires.</summary>
public sealed record SessionRecord(
    string SessionId,
    string UserId,
    string Role,
    string SecurityStamp,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt);

/// <summary>The account as it is now in the user store (read from MySQL with BE-005's reader account).</summary>
public sealed record UserSecurityState(
    string UserId,
    bool IsActive,
    DateTimeOffset? LockoutEnd,
    string Role,
    string SecurityStamp);

/// <summary>Why a session was refused. Logged as a category; never shown to the client in detail.</summary>
public enum SessionRejectReason
{
    None = 0,
    MissingSession,
    Revoked,
    AbsoluteLifetimeExceeded,
    IdleTimeoutExceeded,
    UserNotFound,
    UserDisabled,
    UserLocked,
    SecurityStampChanged,
    RoleChanged,
    UnknownRole,
    UserMismatch,
    UserStoreUnavailable
}

/// <summary>Result of checking one request's session.</summary>
public sealed record SessionCheck(bool IsValid, SessionRejectReason Reason)
{
    public static readonly SessionCheck Valid = new(true, SessionRejectReason.None);

    public static SessionCheck Reject(SessionRejectReason reason) => new(false, reason);
}
