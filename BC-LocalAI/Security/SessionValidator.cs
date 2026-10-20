namespace BC_LocalAI.Security;

/// <summary>
/// Decides whether a session may still be used. Pure: no I/O, so every rule is unit-tested with fixed times.
/// Order matters: the cheapest, most certain refusals come first; the account is checked on every request.
/// </summary>
public static class SessionValidator
{
    public static SessionCheck Check(
        SessionRecord? session,
        string? cookieUserId,
        UserSecurityState? user,
        DateTimeOffset now,
        BcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (session is null)
        {
            return SessionCheck.Reject(SessionRejectReason.MissingSession);
        }

        if (session.RevokedAt is not null)
        {
            return SessionCheck.Reject(SessionRejectReason.Revoked);
        }

        // The cookie must belong to the session it names; a cookie pointing at someone else's session id is refused.
        if (string.IsNullOrEmpty(cookieUserId) || !string.Equals(cookieUserId, session.UserId, StringComparison.Ordinal))
        {
            return SessionCheck.Reject(SessionRejectReason.UserMismatch);
        }

        if (now - session.CreatedAt >= options.AbsoluteLifetime)
        {
            return SessionCheck.Reject(SessionRejectReason.AbsoluteLifetimeExceeded);
        }

        if (now - session.LastSeenAt >= options.IdleTimeout)
        {
            return SessionCheck.Reject(SessionRejectReason.IdleTimeoutExceeded);
        }

        if (user is null)
        {
            return SessionCheck.Reject(SessionRejectReason.UserNotFound);
        }

        if (!string.Equals(user.UserId, session.UserId, StringComparison.Ordinal))
        {
            return SessionCheck.Reject(SessionRejectReason.UserMismatch);
        }

        if (!user.IsActive)
        {
            return SessionCheck.Reject(SessionRejectReason.UserDisabled);
        }

        if (user.LockoutEnd is { } lockoutEnd && lockoutEnd > now)
        {
            return SessionCheck.Reject(SessionRejectReason.UserLocked);
        }

        // Password, status or permission changes rotate the stamp: every older session ends.
        if (!string.Equals(user.SecurityStamp, session.SecurityStamp, StringComparison.Ordinal))
        {
            return SessionCheck.Reject(SessionRejectReason.SecurityStampChanged);
        }

        if (!BcRoles.IsKnown(user.Role))
        {
            return SessionCheck.Reject(SessionRejectReason.UnknownRole);
        }

        // A role change takes effect at once instead of waiting for the cookie to expire.
        if (!string.Equals(user.Role, session.Role, StringComparison.Ordinal))
        {
            return SessionCheck.Reject(SessionRejectReason.RoleChanged);
        }

        return SessionCheck.Valid;
    }
}
