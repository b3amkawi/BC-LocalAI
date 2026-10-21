using System.Collections.Concurrent;
using BC_LocalAI.Security;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BC_LocalAI.Tests.Security;

/// <summary>Clock the tests move by hand; the cookie handler and the session checks both use it.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Stand-in for BE-005's MySQL-backed user store. Tests change accounts while sessions are open.</summary>
public sealed class FakeUserStore : IUserSecurityStateSource
{
    public ConcurrentDictionary<string, UserSecurityState> Users { get; } = new();

    public bool Unavailable { get; set; }

    public int Reads;

    public Task<UserSecurityState?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        if (Unavailable) throw new InvalidOperationException("MySQL unreachable (simulated)");
        return Task.FromResult(Users.TryGetValue(userId, out var user) ? user : null);
    }

    public void Put(string userId, string role = BcRoles.User, bool active = true, string stamp = "S1", DateTimeOffset? lockout = null) =>
        Users[userId] = new UserSecurityState(userId, active, lockout, role, stamp);
}

/// <summary>
/// Test-only endpoints (in the test assembly, added only to these hosts): BE-005's login is not merged yet,
/// so these sign in through the same SessionService that BE-005's login will call.
/// </summary>
[ApiController]
[Route("api/__test")]
public sealed class SecurityTestController(SessionService sessions, FakeUserStore users) : ControllerBase
{
    [HttpPost("signin/{userId}")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn(string userId)
    {
        await sessions.SignInAsync(HttpContext, users.Users[userId]);
        return NoContent();
    }

    [HttpPost("signout")]
    [AllowAnonymous]
    public async Task<IActionResult> SignOutNow()
    {
        await sessions.SignOutAsync(HttpContext);
        return NoContent();
    }

    [HttpPost("revoke-all/{userId}")]
    [AllowAnonymous]
    public async Task<IActionResult> RevokeAll(string userId) => Ok(await sessions.RevokeAllForUserAsync(userId));

    [HttpGet("admin")]
    [Authorize(Policy = BcPolicies.Admin)]
    public IActionResult AdminOnly() => Ok("admin");
}

internal sealed record SecurityHost(WebApplicationFactory<Program> Factory, FakeUserStore Users, ManualClock Clock, CapturingLoggerProvider Logs)
{
    public static readonly DateTimeOffset Start = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    public static SecurityHost Create(WebApplicationFactory<Program> factory, bool enabled = true, bool registerUserStore = true)
    {
        var users = new FakeUserStore();
        var clock = new ManualClock(Start);
        var logs = new CapturingLoggerProvider();
        var keyPath = Path.Combine(Path.GetTempPath(), "bc-be006-test-keys");
        Directory.CreateDirectory(keyPath);
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama();
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = enabled ? "true" : "false",
                // BE-005 validates these when enabled; no database is contacted (the user store is replaced below).
                ["Authentication:KeyDirectory"] = keyPath,
                ["Authentication:ReaderConnectionString"] = "Server=localhost;Database=synthetic;User ID=bc_auth_reader;Password=placeholder;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4", // ci-allow-secret: fake placeholder test configuration, no database is contacted.
                ["Authentication:WriterConnectionString"] = "Server=localhost;Database=synthetic;User ID=bc_auth_attempt_writer;Password=placeholder-writer;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4" // ci-allow-secret: fake placeholder writer configuration, no database is contacted.
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IAIService, MockAIService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(users);
                services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                services.RemoveAll<IUserSecurityStateSource>(); // never the MySQL source in these tests
                if (registerUserStore)
                {
                    services.AddSingleton<IUserSecurityStateSource>(users);
                }

                services.AddControllers().AddApplicationPart(typeof(SecurityTestController).Assembly);
            });
        });
        return new SecurityHost(host, users, clock, logs);
    }

    /// <summary>HTTPS, so the Secure cookie is sent back; cookies kept like a browser; no redirects followed.</summary>
    public HttpClient Browser() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>No cookie handling: the test sets the Cookie header itself (replay of a copied cookie).</summary>
    public HttpClient Raw() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = false
    });
}
