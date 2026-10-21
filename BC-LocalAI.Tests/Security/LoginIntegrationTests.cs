using System.Net;
using System.Security.Claims;
using System.Text.Json;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Security;
using BC_LocalAI.Services.Authentication;
using BC_LocalAI.Tests.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests.Security;

/// <summary>BE-005's real login endpoints running on BE-006's sessions.</summary>
public sealed class LoginIntegrationTests
{
    private const string Id = "00000000-0000-0000-0000-000000000005";

    private static StubAuthenticationBackend Backend(string role = BcRoles.User) =>
        new(AuthenticationResult.Succeeded(new(Id, "ผู้ใช้สมมติ", "synthetic", [role])));

    [Fact]
    public async Task Login_ThenAccountDisabled_MeAndChatStopAtTheNextRequest()
    {
        var accounts = new FakeUserStore();
        accounts.Put(Id);
        await using var factory = AuthenticationApiTests.Factory(Backend(), accounts: accounts);
        using var client = AuthenticationApiTests.Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthenticationApiTests.Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        accounts.Put(Id, active: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_TheRoleComesFromTheAccount_NotFromTheLoginBackend()
    {
        var accounts = new FakeUserStore();
        accounts.Put(Id, role: BcRoles.User);
        // The backend claims Admin; the account in the user store is a User.
        await using var factory = AuthenticationApiTests.Factory(Backend(BcRoles.Admin), accounts: accounts);
        using var client = AuthenticationApiTests.Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthenticationApiTests.Login(client)).StatusCode);

        using var me = JsonDocument.Parse(await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        var roles = me.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();

        Assert.Equal([BcRoles.User], roles);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("unknown-role")]
    public async Task Login_AccountChangedAfterThePasswordCheck_IsTheSameGeneric401_AndNoCookie(string state)
    {
        var accounts = new FakeUserStore();
        if (state == "disabled") accounts.Put(Id, active: false);
        if (state == "unknown-role") accounts.Put(Id, role: "KnowledgeManager");
        await using var factory = AuthenticationApiTests.Factory(Backend(), accounts: accounts);
        using var client = AuthenticationApiTests.Client(factory);

        var login = await AuthenticationApiTests.Login(client);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("AUTH_INVALID_CREDENTIALS", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(login.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith(BcAuthentication.CookieName + "=", StringComparison.Ordinal) && !cookie.StartsWith(BcAuthentication.CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}

public sealed class LoginFailClosedTests
{
    private const string Id = "00000000-0000-0000-0000-000000000005";

    /// <summary>The MySQL source turns every database failure into this exception (no SQL or connection details).</summary>
    private sealed class DownUserStore : IUserSecurityStateSource
    {
        public Task<UserSecurityState?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new AuthenticationDependencyException();
    }

    private static async Task<string> Code(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task SessionsFull_LoginIs503_AndTheSignedInUserKeepsTheirSession()
    {
        await using var factory = AuthenticationApiTests.Factory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Authentication:MaxSessions"] = "1" })));
        using var first = AuthenticationApiTests.Client(factory);
        using var second = AuthenticationApiTests.Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthenticationApiTests.Login(first)).StatusCode);

        var refused = await AuthenticationApiTests.Login(second);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("AUTH_UNAVAILABLE", await Code(refused));
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AccountDatabaseDownAtSignIn_LoginIs503_NoCookie()
    {
        await using var factory = AuthenticationApiTests.Factory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserSecurityStateSource>();
                services.AddSingleton<IUserSecurityStateSource, DownUserStore>();
            }));
        using var client = AuthenticationApiTests.Client(factory);

        var login = await AuthenticationApiTests.Login(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal("AUTH_UNAVAILABLE", await Code(login));
        Assert.DoesNotContain(login.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith(BcAuthentication.CookieName + "=", StringComparison.Ordinal) && !cookie.StartsWith(BcAuthentication.CookieName + "=;", StringComparison.Ordinal));
    }
}

public sealed class SessionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static (SessionService Service, FakeUserStore Users, InMemorySessionStore Store) Create()
    {
        var users = new FakeUserStore();
        var store = new InMemorySessionStore();
        var service = new SessionService(store, users, new ManualClock(T0), Options.Create(new BcAuthenticationOptions()), NullLogger<SessionService>.Instance);
        return (service, users, store);
    }

    private static HttpContext Context()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService, RecordingAuthenticationService>()
            .BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services };
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("locked")]
    [InlineData("unknown-role")]
    [InlineData("no-stamp")]
    public async Task SignInByUserId_IsRefused_ForAnAccountThatMayNotSignIn(string state)
    {
        var (service, users, store) = Create();
        switch (state)
        {
            case "inactive": users.Put("u-1", active: false); break;
            case "locked": users.Put("u-1", lockout: T0.AddMinutes(5)); break;
            case "unknown-role": users.Put("u-1", role: "KnowledgeManager"); break;
            case "no-stamp": users.Put("u-1", stamp: ""); break;
        }

        Assert.False(await service.SignInAsync(Context(), "u-1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ProfileClaims_CannotOverrideIdentityRoleOrSession()
    {
        var (service, users, _) = Create();
        users.Put("u-1", role: BcRoles.User);
        var context = Context();

        Assert.True(await service.SignInAsync(context, "u-1",
        [
            new Claim(ClaimTypes.Role, BcRoles.Admin),
            new Claim(ClaimTypes.NameIdentifier, "someone-else"),
            new Claim(BcClaimTypes.SessionId, "forged"),
            new Claim(ClaimTypes.Name, "synthetic")
        ]));

        var principal = ((RecordingAuthenticationService)context.RequestServices.GetService(typeof(Microsoft.AspNetCore.Authentication.IAuthenticationService))!).SignedIn!;
        Assert.Equal(["User"], principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
        Assert.Equal(["u-1"], principal.FindAll(ClaimTypes.NameIdentifier).Select(c => c.Value));
        Assert.Single(principal.FindAll(BcClaimTypes.SessionId));
        Assert.NotEqual("forged", principal.FindFirstValue(BcClaimTypes.SessionId));
        Assert.Equal("synthetic", principal.Identity!.Name);
    }

    private sealed class RecordingAuthenticationService : Microsoft.AspNetCore.Authentication.IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties)
        {
            SignedIn = principal;
            return Task.CompletedTask;
        }

        public Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();
        public Task ChallengeAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
    }
}

public sealed class SessionCapacityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static InMemorySessionStore Store(int max) => new(
        Options.Create(new BC_LocalAI.Configuration.AuthenticationOptions { MaxSessions = max }),
        Options.Create(new BcAuthenticationOptions()));

    [Fact]
    public async Task Full_FailsClosed_WithTheDependencyError_LoginMapsTo503()
    {
        var store = Store(2);
        await store.CreateAsync("u-1", BcRoles.User, "S", T0);
        await store.CreateAsync("u-2", BcRoles.User, "S", T0);

        await Assert.ThrowsAsync<AuthenticationDependencyException>(() => store.CreateAsync("u-3", BcRoles.User, "S", T0));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Full_ReclaimsRevokedSessions()
    {
        var store = Store(1);
        var first = await store.CreateAsync("u-1", BcRoles.User, "S", T0);
        await store.RevokeAsync(first.SessionId, T0);

        Assert.NotNull(await store.CreateAsync("u-2", BcRoles.User, "S", T0));
        Assert.Equal(1, store.Count);
    }

    [Theory]
    [InlineData(30, 0)]   // idle 30 minutes
    [InlineData(480, 1)]  // 8 hours, even if it was active a minute ago
    public async Task Full_ReclaimsExpiredSessions(int minutesLater, int minutesSinceActivity)
    {
        var store = Store(1);
        var first = await store.CreateAsync("u-1", BcRoles.User, "S", T0);
        var now = T0.AddMinutes(minutesLater);
        if (minutesSinceActivity > 0) await store.TouchAsync(first.SessionId, now.AddMinutes(-minutesSinceActivity));

        Assert.NotNull(await store.CreateAsync("u-2", BcRoles.User, "S", now));
        Assert.Null(await store.GetAsync(first.SessionId));
    }

    [Fact]
    public async Task Full_KeepsLiveSessions()
    {
        var store = Store(1);
        var first = await store.CreateAsync("u-1", BcRoles.User, "S", T0);

        await Assert.ThrowsAsync<AuthenticationDependencyException>(() => store.CreateAsync("u-2", BcRoles.User, "S", T0.AddMinutes(29)));
        Assert.NotNull(await store.GetAsync(first.SessionId));
    }
}
