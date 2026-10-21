using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// Re-checks the caller's session while a long answer is being produced, so an answer stops as soon as the session ends
/// (sign-out, revoke-all, 8 h reached) and within <see cref="AccountCheckInterval"/> of the account being disabled, locked,
/// deleted or changed in the database. The cookie check at the start of the request is not enough for a 180 s stream.
/// One instance per request.
/// </summary>
public sealed class StreamSessionGuard(
    ISessionStore store,
    IServiceProvider services,
    TimeProvider clock,
    IOptions<BcAuthenticationOptions> options,
    ILogger<StreamSessionGuard> logger)
{
    /// <summary>
    /// The server-side session (in memory) is checked before every piece of the answer; the account (one MySQL query)
    /// at most this often, counted from the check made when the request started.
    /// </summary>
    public static readonly TimeSpan AccountCheckInterval = TimeSpan.FromSeconds(5);

    // Created with the controller, right after the cookie check of this request read the account.
    private DateTimeOffset _lastAccountCheck = clock.GetUtcNow();

    /// <summary>False when the answer must stop: the session is then revoked (unless the user store is down) and logged.</summary>
    public async Task<bool> IsStillValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return true;
        }

        var sessionId = principal.FindFirstValue(BcClaimTypes.SessionId);
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var now = clock.GetUtcNow();

        var session = sessionId is null ? null : await store.GetAsync(sessionId, cancellationToken);
        // While the answer is being produced the user is active: the idle timeout cannot end the session mid-answer.
        var active = session is null ? null : session with { LastSeenAt = now };

        UserSecurityState? user = null;
        var checkAccount = now - _lastAccountCheck >= AccountCheckInterval;
        if (active is not null && checkAccount)
        {
            try
            {
                var source = services.GetService<IUserSecurityStateSource>()
                    ?? throw new InvalidOperationException("No IUserSecurityStateSource is registered.");
                user = await source.GetAsync(active.UserId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Fail closed, but do not revoke: the account may be fine once the database is back.
                SecurityLog.UserStoreUnavailable(logger, active.UserId, exception.GetType().Name);
                return false;
            }

            _lastAccountCheck = now;
        }

        var check = SessionValidator.Check(active, userId, user, now, options.Value);
        if (check.IsValid || (!checkAccount && check.Reason == SessionRejectReason.UserNotFound))
        {
            // Without an account check due, every session rule up to the account itself passed.
            return true;
        }

        if (session is not null && session.RevokedAt is null)
        {
            await store.RevokeAsync(session.SessionId, now, CancellationToken.None);
        }

        SecurityLog.SessionRejected(logger, check.Reason, userId ?? "unknown");
        return false;
    }
}
