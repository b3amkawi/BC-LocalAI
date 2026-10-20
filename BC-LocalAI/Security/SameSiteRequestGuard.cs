using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// CSRF defence for cookie-authenticated APIs (active only when Authentication:Enabled is true).
/// An unsafe /api request (POST, PUT, PATCH, DELETE) that a browser marks as coming from another site is refused with
/// 403 AUTH_CSRF_INVALID. Requests without browser headers (scripts, tests) are not affected: CSRF needs a browser.
/// This works together with SameSite=Lax cookies and JSON-only bodies; BE-005 adds antiforgery tokens for its own endpoints.
/// </summary>
public sealed class SameSiteRequestGuard(RequestDelegate next, IOptions<BcAuthenticationOptions> options, ILogger<SameSiteRequestGuard> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (options.Value.Enabled
            && context.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && IsCrossSite(context.Request))
        {
            SecurityLog.CrossSiteRequestRefused(logger, context.Request.Method, context.Request.Path.Value ?? "");
            await BcCookieEvents.WriteErrorAsync(context, StatusCodes.Status403Forbidden, "AUTH_CSRF_INVALID", "Request refused");
            return;
        }

        await next(context);
    }

    public static bool IsCrossSite(HttpRequest request)
    {
        // Fetch Metadata (all current browsers): only same-origin requests and direct navigations are accepted.
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none"))
        {
            return true;
        }

        // Origin, for browsers without Fetch Metadata: it must be exactly this site (scheme, host and port).
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return false;
        }

        if (origin == "null" || !Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return true;
        }

        return !string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
