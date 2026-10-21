using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Controllers;

// Mock-only FE-003 groundwork, not a Login endpoint or an authorization control for Chat.
[AllowAnonymous]
public sealed class LoginPreviewController(IHostEnvironment environment, IConfiguration configuration) : Controller
{
    [HttpGet("/preview/login")]
    public IActionResult Index()
    {
        Response.Headers.CacheControl = "no-store";
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("FrontendPreview:Enabled") ||
            address is null || !IPAddress.IsLoopback(address)) return NotFound();

        // Even accidental native form submission/fetch cannot send preview credentials to a server.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; " +
            "img-src 'self'; connect-src 'none'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return View();
    }
}
