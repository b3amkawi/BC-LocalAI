using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Controllers
{
    public class HomeController(IOptions<BcAuthenticationOptions> authentication, IAuthorizationService authorization) : Controller
    {
        // The Chat UI (FE-001). The Content-Security-Policy keeps the page on its own origin: scripts and
        // styles only from this site, and the browser may only call this site (so never Ollama on :11434).
        private const string ChatContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

        public async Task<IActionResult> Index()
        {
            if (authentication.Value.Enabled)
            {
                FrontendPageSecurity.Apply(Response);
                if (!Request.IsHttps) return StatusCode(403, "ต้องเชื่อมต่อผ่าน HTTPS");
                if (User.Identity?.IsAuthenticated != true) return Redirect("/login");
                if (!(await authorization.AuthorizeAsync(User, null, BcPolicies.ChatUser)).Succeeded)
                    return StatusCode(403, "ไม่มีสิทธิ์ใช้หน้านี้");
                return View("Authenticated");
            }
            Response.Headers.ContentSecurityPolicy = ChatContentSecurityPolicy;
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
