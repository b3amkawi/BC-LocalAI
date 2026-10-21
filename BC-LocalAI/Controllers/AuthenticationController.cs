using System.Security.Claims;
using System.Text.Json;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Security;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Controllers;

[Route("api/auth")]
public sealed class AuthenticationController(IAuthenticationBackend backend, LoginAttemptLimiter limiter, IAntiforgery antiforgery,
    SessionService sessions) : ControllerBase
{
    public const int MaxBodyBytes = 16 * 1024;

    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost("login"), AllowAnonymous]
    public async Task Login(CancellationToken cancellationToken)
    {
        var (request, oversized) = await ReadRequest(cancellationToken);
        if (oversized) { await Error(413, "REQUEST_TOO_LARGE", "คำขอมีขนาดใหญ่เกินกำหนด"); return; }
        if (AuthenticationRequestValidator.Validate(request) is not null)
        { await Error(400, "AUTH_INVALID_REQUEST", "ข้อมูลเข้าสู่ระบบไม่ถูกต้อง"); return; }

        // Do not trust X-Forwarded-For: this deployment has no approved reverse proxy configuration.
        using var lease = limiter.TryAcquire(request!.Username!, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (lease is null)
        {
            Response.Headers.RetryAfter = "60";
            await Error(429, "AUTH_RATE_LIMITED", "มีคำขอเข้าสู่ระบบมากเกินไป กรุณาลองใหม่ภายหลัง");
            return;
        }
        var result = await backend.AuthenticateAsync(request, cancellationToken);
        if (result.Outcome == AuthenticationOutcome.Unavailable)
        { await Error(503, "AUTH_UNAVAILABLE", "ระบบเข้าสู่ระบบไม่พร้อมใช้งาน"); return; }
        if (result.Outcome != AuthenticationOutcome.Success || result.User is null)
        { await Error(401, "AUTH_INVALID_CREDENTIALS", "ชื่อบัญชีหรือรหัสผ่านไม่ถูกต้อง"); return; }
        var user = result.User;
        // Re-login replaces the current server-side session; no session fixation or orphaned old cookie.
        await sessions.SignOutAsync(HttpContext, cancellationToken);
        // BE-006: a new server-side session; role and security stamp come from the account's current state in MySQL.
        Claim[] profile = [new Claim(ClaimTypes.Name, user.Username), new Claim("display_name", user.DisplayName)];
        if (!await sessions.SignInAsync(HttpContext, user.UserId, profile, cancellationToken))
        {
            // The account changed between the password check and sign-in (disabled, locked, role): same generic answer.
            await Error(401, "AUTH_INVALID_CREDENTIALS", "ชื่อบัญชีหรือรหัสผ่านไม่ถูกต้อง");
            return;
        }
        ClearCsrf();
        await Response.WriteAsJsonAsync(new CurrentUserResponse(user), cancellationToken);
    }

    [HttpGet("me"), Authorize(AuthenticationSchemes = AuthenticationRegistration.Scheme)]
    public IActionResult Me() => Ok(new CurrentUserResponse(new AuthenticatedUser(
        User.FindFirstValue(ClaimTypes.NameIdentifier)!, User.FindFirstValue("display_name") ?? "",
        User.Identity?.Name ?? "", User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray())));

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        // BE-006: revokes the server-side session (a copied cookie stops working) and clears the cookie.
        await sessions.SignOutAsync(HttpContext);
        ClearCsrf();
        return NoContent();
    }

    private void ClearCsrf() => Response.Cookies.Delete("__Host-BC.Csrf", new CookieOptions
    { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });

    private Task Error(int status, string code, string message) => AuthenticationRegistration.Error(Response, status, code, message);

    private async Task<(LoginRequest? Request, bool Oversized)> ReadRequest(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxBodyBytes) return (null, true);
        if (Request.ContentType is null || !Request.ContentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return (null, false);
        // Bound actual bytes, not just Content-Length: chunked bodies get the same hard ceiling before JSON parsing.
        var bytes = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await Request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxBodyBytes) return (null, true);
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, false);
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Count(p => p.Name == "username") != 1 || properties.Count(p => p.Name == "password") != 1 ||
                properties.Any(p => p.Value.ValueKind != JsonValueKind.String)) return (null, false);
            return (new LoginRequest { Username = document.RootElement.GetProperty("username").GetString(), Password = document.RootElement.GetProperty("password").GetString() }, false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return (null, false); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
