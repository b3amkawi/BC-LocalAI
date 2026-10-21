using BC_LocalAI.Configuration;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Middleware;

public sealed class AuthenticationBoundaryMiddleware(RequestDelegate next, ILogger<AuthenticationBoundaryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IOptions<AuthenticationOptions> options, IAntiforgery antiforgery)
    {
        if (!context.Request.Path.StartsWithSegments("/api/auth")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!options.Value.Enabled) { context.Response.StatusCode = 404; return; }
        if (!context.Request.IsHttps)
        {
            await AuthenticationRegistration.Error(context.Response, 403, "AUTH_HTTPS_REQUIRED", "ต้องเชื่อมต่อผ่าน HTTPS");
            return;
        }
        if (context.Request.Headers["Sec-Fetch-Site"].Any(value => value is "cross-site" or "same-site") ||
            (context.Request.Headers.TryGetValue("Origin", out var origin) &&
             (origin.Count != 1 || !Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) ||
              !Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var target) ||
              !string.Equals(uri.GetLeftPart(UriPartial.Authority), target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))))
        {
            await AuthenticationRegistration.Error(context.Response, 403, "AUTH_CSRF_INVALID", "คำขอไม่ผ่านการตรวจสอบความปลอดภัย");
            return;
        }
        try
        {
            // Antiforgery accepts form tokens too; force header-only JSON mutations and never read credentials as a form.
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                if (context.Request.Headers["X-CSRF-TOKEN"].Count != 1 ||
                    !await antiforgery.IsRequestValidAsync(context))
                {
                    await AuthenticationRegistration.Error(context.Response, 403, "AUTH_CSRF_INVALID", "คำขอไม่ผ่านการตรวจสอบความปลอดภัย");
                    return;
                }
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (AuthenticationDependencyException)
        {
            if (context.Response.HasStarted) throw;
            await AuthenticationRegistration.Error(context.Response, 503, "AUTH_UNAVAILABLE", "ระบบเข้าสู่ระบบไม่พร้อมใช้งาน");
        }
        catch (Exception)
        {
            // Do not send raw exceptions to the outer middleware logger: dependencies may contain credentials/SQL.
            logger.LogError(new EventId(5102, "AuthenticationFailure"), "Unexpected authentication failure. TraceId: {TraceId}", context.TraceIdentifier);
            if (context.Response.HasStarted) throw;
            await AuthenticationRegistration.Error(context.Response, 500, "INTERNAL_ERROR", "เกิดข้อผิดพลาดภายในระบบ");
        }
    }
}
