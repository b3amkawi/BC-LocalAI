using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// Starts and ends sessions. BE-005's login endpoint calls <see cref="SignInAsync"/> after the password is verified;
/// its logout endpoint calls <see cref="SignOutAsync"/>. Account administration calls <see cref="RevokeAllForUserAsync"/>.
/// </summary>
public sealed class SessionService(
    ISessionStore store,
    TimeProvider clock,
    IOptions<BcAuthenticationOptions> options,
    ILogger<SessionService> logger)
{
    /// <summary>Creates a new server-side session (never reusing an old one) and issues the cookie.</summary>
    /// <exception cref="InvalidOperationException">The account may not sign in (inactive, locked or unknown role).</exception>
    public async Task SignInAsync(HttpContext httpContext, UserSecurityState user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.GetUtcNow();
        if (!user.IsActive || user.LockoutEnd > now || !BcRoles.IsKnown(user.Role))
        {
            throw new InvalidOperationException("This account cannot sign in. The login endpoint must check it before calling SignInAsync.");
        }

        // A session id from before sign-in (if any) is never carried over: session fixation is not possible.
        var session = await store.CreateAsync(user.UserId, user.Role, user.SecurityStamp, now, cancellationToken);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.UserId),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim(BcClaimTypes.SessionId, session.SessionId)
            ],
            BcAuthentication.Scheme,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role);

        await httpContext.SignInAsync(
            BcAuthentication.Scheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false, // ends with the browser session as well
                AllowRefresh = false, // the server-side session, not the cookie, decides idle and absolute expiry
                IssuedUtc = now,
                ExpiresUtc = now + options.Value.AbsoluteLifetime
            });

        SecurityLog.SessionCreated(logger, user.UserId, user.Role);
    }

    /// <summary>Revokes the current session on the server and clears the cookie. Safe to call when signed out.</summary>
    public async Task SignOutAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var sessionId = httpContext.User.FindFirstValue(BcClaimTypes.SessionId);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sessionId is not null)
        {
            await store.RevokeAsync(sessionId, clock.GetUtcNow(), cancellationToken);
        }

        await httpContext.SignOutAsync(BcAuthentication.Scheme);
        if (userId is not null)
        {
            SecurityLog.SessionSignedOut(logger, userId);
        }
    }

    /// <summary>Ends every session of a user at once (account disabled, password reset, incident).</summary>
    public async Task<int> RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var count = await store.RevokeAllForUserAsync(userId, clock.GetUtcNow(), cancellationToken);
        SecurityLog.SessionsRevokedForUser(logger, userId, count);
        return count;
    }
}
