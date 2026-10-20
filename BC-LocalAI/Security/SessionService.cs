using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// Starts and ends sessions. BE-005's login calls <see cref="SignInAsync(HttpContext, string, IEnumerable{Claim}?, CancellationToken)"/>
/// after the password is verified; logout calls <see cref="SignOutAsync"/>; account administration calls <see cref="RevokeAllForUserAsync"/>.
/// </summary>
public sealed class SessionService(
    ISessionStore store,
    IUserSecurityStateSource users,
    TimeProvider clock,
    IOptions<BcAuthenticationOptions> options,
    ILogger<SessionService> logger)
{
    // Claims that only this service may set: profile claims from the caller can never override identity, role or session.
    private static readonly HashSet<string> ReservedClaimTypes = [ClaimTypes.NameIdentifier, ClaimTypes.Role, BcClaimTypes.SessionId];

    /// <summary>
    /// Signs in the account with the given id. Its role and security stamp are read from the user store now, never taken
    /// from the caller, so the session starts from the account's current state.
    /// </summary>
    /// <returns>false when the account does not exist (any more) or may not sign in (inactive, locked, unknown role).</returns>
    public async Task<bool> SignInAsync(HttpContext httpContext, string userId, IEnumerable<Claim>? profileClaims = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var user = await users.GetAsync(userId, cancellationToken);
        if (user is null || !CanSignIn(user, clock.GetUtcNow()))
        {
            return false;
        }

        await SignInAsync(httpContext, user, profileClaims, cancellationToken);
        return true;
    }

    /// <summary>Creates a new server-side session (never reusing an old one) and issues the cookie.</summary>
    /// <exception cref="InvalidOperationException">The account may not sign in (inactive, locked or unknown role).</exception>
    public async Task SignInAsync(HttpContext httpContext, UserSecurityState user, IEnumerable<Claim>? profileClaims = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.GetUtcNow();
        if (!CanSignIn(user, now))
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
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaims((profileClaims ?? []).Where(claim => !ReservedClaimTypes.Contains(claim.Type)));

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

    private static bool CanSignIn(UserSecurityState user, DateTimeOffset now) =>
        user.IsActive && !(user.LockoutEnd > now) && BcRoles.IsKnown(user.Role) && !string.IsNullOrEmpty(user.SecurityStamp);
}
