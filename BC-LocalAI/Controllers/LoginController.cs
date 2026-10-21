using BC_LocalAI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Controllers;

[AllowAnonymous]
public sealed class LoginController(IOptions<BcAuthenticationOptions> authentication) : Controller
{
    [HttpGet("/login")]
    public IActionResult Index()
    {
        FrontendPageSecurity.Apply(Response);
        if (!authentication.Value.Enabled) return NotFound();
        if (!Request.IsHttps) return StatusCode(403, "ต้องเปิดหน้านี้ผ่าน HTTPS จึงจะเข้าสู่ระบบได้");
        // Fixed local destination only: never reflect an untrusted returnUrl into a redirect.
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        return View();
    }
}
