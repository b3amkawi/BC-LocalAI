using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace BC_LocalAI.Controllers
{
    public class HomeController : Controller
    {
        // The Chat UI (FE-001). The Content-Security-Policy keeps the page on its own origin: scripts and
        // styles only from this site, and the browser may only call this site (so never Ollama on :11434).
        private const string ChatContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

        public IActionResult Index()
        {
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
