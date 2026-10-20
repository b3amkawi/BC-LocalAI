using System.Security.Claims;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// Checks the server-side session and the current account on every request that carries the cookie,
/// and turns the cookie handler's redirects into the API's JSON 401 / 403 errors.
/// </summary>
public sealed class BcCookieEvents(
    ISessionStore store,
    TimeProvider clock,
    IOptions<BcAuthenticationOptions> options,
    IServiceProvider services,
    ILogger<BcCookieEvents> logger) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var sessionId = principal?.FindFirstValue(BcClaimTypes.SessionId);
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var now = clock.GetUtcNow();
        var cancellationToken = context.HttpContext.RequestAborted;

        var session = sessionId is null ? null : await store.GetAsync(sessionId, cancellationToken);

        SessionCheck check;
        if (session is null || session.RevokedAt is not null || userId is null)
        {
            check = SessionValidator.Check(session, userId, null, now, options.Value);
        }
        else
        {
            UserSecurityState? user;
            try
            {
                // No user store means authentication cannot be trusted: refuse (fail closed).
                var source = services.GetService<IUserSecurityStateSource>()
                    ?? throw new InvalidOperationException("No IUserSecurityStateSource is registered.");
                user = await source.GetAsync(session.UserId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SecurityLog.UserStoreUnavailable(logger, session.UserId, exception.GetType().Name);
                await RejectAsync(context, SessionRejectReason.UserStoreUnavailable, session.UserId, revoke: null);
                return;
            }

            check = SessionValidator.Check(session, userId, user, now, options.Value);
        }

        if (!check.IsValid)
        {
            await RejectAsync(context, check.Reason, userId ?? "unknown", revoke: session?.RevokedAt is null ? session : null);
            return;
        }

        await store.TouchAsync(session!.SessionId, now, cancellationToken);
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, SessionRejectReason reason, string userId, SessionRecord? revoke)
    {
        // A session refused for its own reasons (expiry, account change) is revoked, so it can never come back.
        if (revoke is not null)
        {
            await store.RevokeAsync(revoke.SessionId, clock.GetUtcNow(), CancellationToken.None);
        }

        SecurityLog.SessionRejected(logger, reason, userId);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(BcAuthentication.Scheme);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        WriteErrorAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "AUTH_REQUIRED", "Sign-in required");

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        SecurityLog.AccessDenied(
            logger,
            context.Request.Path.Value ?? "",
            context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown");
        return WriteErrorAsync(context.HttpContext, StatusCodes.Status403Forbidden, "AUTH_FORBIDDEN", "Access denied");
    }

    // The API never redirects to an HTML page: callers get the same JSON error format as every other API error.
    internal static Task WriteErrorAsync(HttpContext httpContext, int status, string code, string message)
    {
        httpContext.Response.StatusCode = status;
        httpContext.Response.Headers.CacheControl = "no-store";
        return httpContext.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(code, message)));
    }
}
