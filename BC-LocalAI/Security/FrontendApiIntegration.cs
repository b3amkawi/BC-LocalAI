using System.Security.Claims;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>FE-003 integration preconditions, never an authentication scheme or a client-selected resource owner.</summary>
public static class FrontendApiIntegration
{
    public const string ExpectedUserHeader = "X-BC-Expected-User";
    public const string ActorHeader = "X-BC-User";

    private static bool Scope(PathString path)
    {
        // ASP.NET routing accepts case variations and trailing slashes; preconditions must cover those aliases too.
        var normalized = path.Value?.TrimEnd('/') ?? "";
        return path.StartsWithSegments("/api/conversations") || new[] { "/api/chat", "/api/chat/stream", "/api/auth/me", "/api/auth/logout" }
            .Any(route => route.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    // Before HTTPS redirection: never redirect a submitted authenticated UI API body onto another request.
    public static IApplicationBuilder UseFrontendApiHttpsGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.RequestServices.GetRequiredService<IOptions<BcAuthenticationOptions>>().Value.Enabled && !context.Request.IsHttps &&
            Scope(context.Request.Path) && (context.Request.Headers.ContainsKey(ExpectedUserHeader) ||
            (context.Request.Path.StartsWithSegments("/api/conversations") &&
                context.RequestServices.GetRequiredService<IOptions<ChatHistoryOptions>>().Value.Enabled)))
        {
            await Error(context, 403, "AUTH_HTTPS_REQUIRED", "ต้องเชื่อมต่อผ่าน HTTPS"); return;
        }
        await next(context);
    });

    // After cookie authentication, before authorization/model binding/DB work.
    public static IApplicationBuilder UseFrontendApiIdentityBoundary(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (Scope(context.Request.Path) && context.User.Identities.Any(identity => identity.IsAuthenticated) &&
            context.RequestServices.GetRequiredService<IOptions<BcAuthenticationOptions>>().Value.Enabled)
        {
            var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (context.Request.Headers.TryGetValue(ExpectedUserHeader, out var expected) &&
                (expected.Count != 1 || actor is null || !string.Equals(expected[0], actor, StringComparison.Ordinal)))
            {
                // A precondition failure, not a choice of owner. Do not revoke a different tab's valid session.
                await Error(context, 401, "AUTH_SESSION_CHANGED", "บัญชีที่เข้าสู่ระบบเปลี่ยนไป กรุณาตรวจสอบเซสชันใหม่"); return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (actor is not null) context.Response.Headers[ActorHeader] = actor;
        }
        await next(context);
    });

    internal static Task Error(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status; context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(code, message)), context.RequestAborted);
    }
}

/// <summary>Cookie-backed history mutations require header antiforgery. Legacy chat without a conversation stays unchanged.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FrontendHistoryMutationAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var mutation = !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method));
        var savesHistory = http.Request.Path.StartsWithSegments("/api/conversations") ||
            context.ActionArguments.Values.OfType<ChatRequest>().Any(request => request.ConversationId is not null);
        if (mutation && savesHistory && http.User.Identities.Any(identity => identity.IsAuthenticated) &&
            http.RequestServices.GetRequiredService<IOptions<BcAuthenticationOptions>>().Value.Enabled)
        {
            var code = !http.Request.IsHttps ? "AUTH_HTTPS_REQUIRED" :
                http.Request.Headers["X-CSRF-TOKEN"].Count != 1 ||
                !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http) ? "AUTH_CSRF_INVALID" : null;
            if (code is not null)
            {
                http.Response.Headers.CacheControl = "no-store";
                context.Result = new ObjectResult(new ApiErrorResponse(new ApiError(code,
                    code == "AUTH_HTTPS_REQUIRED" ? "ต้องเชื่อมต่อผ่าน HTTPS" : "คำขอไม่ผ่านการตรวจสอบความปลอดภัย"))) { StatusCode = 403 };
                return;
            }
        }
        await next();
    }
}
