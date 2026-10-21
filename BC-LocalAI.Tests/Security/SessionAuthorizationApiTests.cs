using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Security;

/// <summary>BE-006 through the real pipeline with Authentication:Enabled = true (and false where stated).</summary>
public sealed class SessionAuthorizationApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static StringContent Json(string body = """{"message":"hello"}""") => new(body, Encoding.UTF8, "application/json");

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task SignIn(HttpClient client, string userId)
    {
        var response = await client.PostAsync($"/api/__test/signin/{userId}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---------------------------------------------------------------- anonymous access

    [Fact]
    public async Task Anonymous_ChatApi_Is401Json_NotARedirect()
    {
        var host = SecurityHost.Create(factory);

        var response = await host.Browser().PostAsync("/api/chat", Json());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("AUTH_REQUIRED", await ErrorCode(response));
    }

    [Fact]
    public async Task Anonymous_StreamApi_Is401Json_BeforeAnyEvent()
    {
        var host = SecurityHost.Create(factory);

        var response = await host.Browser().PostAsync("/api/chat/stream", Json());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("AUTH_REQUIRED", await ErrorCode(response));
    }

    [Fact]
    public async Task ChatPage_StaysReachable_WithoutSignIn()
    {
        var host = SecurityHost.Create(factory);

        var response = await host.Browser().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- signed in

    [Fact]
    public async Task SignedInUser_CanChat()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");

        var response = await client.PostAsync("/api/chat", Json());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_HasTheRequiredFlags_AndNoSecretsInside()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1", stamp: "STAMP-SECRET-123");
        var response = await host.Browser().PostAsync("/api/__test/signin/u-1", null);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var lower = setCookie.ToLowerInvariant();
        Assert.StartsWith(BcAuthentication.CookieName + "=", setCookie);
        Assert.Contains("; secure", lower);
        Assert.Contains("; httponly", lower);
        Assert.Contains("; samesite=lax", lower);
        Assert.Contains("; path=/", lower);
        Assert.DoesNotContain("domain=", lower);
        Assert.DoesNotContain("expires=", lower); // ends with the browser session
        Check.NotLeaked(setCookie, "STAMP-SECRET-123", "u-1"); // the ticket is encrypted by Data Protection
    }

    [Fact]
    public async Task UserRole_CannotReachAdminOnly_403Json_AdminCan()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1", BcRoles.User);
        host.Users.Put("a-1", BcRoles.Admin);
        var user = host.Browser();
        var admin = host.Browser();
        await SignIn(user, "u-1");
        await SignIn(admin, "a-1");

        var denied = await user.GetAsync("/api/__test/admin");
        var allowed = await admin.GetAsync("/api/__test/admin");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        Assert.Equal("AUTH_FORBIDDEN", await ErrorCode(denied));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/chat", Json())).StatusCode); // Admin may also chat
    }

    [Fact]
    public async Task Anonymous_AdminOnly_Is401()
    {
        var host = SecurityHost.Create(factory);

        var response = await host.Browser().GetAsync("/api/__test/admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------- revocation

    [Fact]
    public async Task AfterSignOut_ACopiedCookie_IsUseless()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var signIn = await host.Raw().PostAsync("/api/__test/signin/u-1", null);
        var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie")).Split(';')[0];

        HttpRequestMessage WithCookie(string path) => new(HttpMethod.Post, path) { Content = Json(), Headers = { { "Cookie", cookie } } };
        Assert.Equal(HttpStatusCode.OK, (await host.Raw().SendAsync(WithCookie("/api/chat"))).StatusCode);

        var signOut = new HttpRequestMessage(HttpMethod.Post, "/api/__test/signout") { Headers = { { "Cookie", cookie } } };
        Assert.Equal(HttpStatusCode.NoContent, (await host.Raw().SendAsync(signOut)).StatusCode);

        var replay = await host.Raw().SendAsync(WithCookie("/api/chat")); // the stolen copy still has a valid signature
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task RevokeAllForUser_EndsEveryDevice_ButNotOtherUsers()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        host.Users.Put("u-2");
        var laptop = host.Browser();
        var phone = host.Browser();
        var other = host.Browser();
        await SignIn(laptop, "u-1");
        await SignIn(phone, "u-1");
        await SignIn(other, "u-2");

        var revoked = await host.Browser().PostAsync("/api/__test/revoke-all/u-1", null);
        Assert.Equal("2", await revoked.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.PostAsync("/api/chat", Json())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync("/api/chat", Json())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync("/api/chat", Json())).StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stamp")]
    [InlineData("role")]
    [InlineData("deleted")]
    public async Task AccountChange_EndsTheOpenSession_AtTheNextRequest_AndForGood(string change)
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        // A copied cookie (as an attacker would keep it): the browser deleting its own cookie proves nothing,
        // only a server-side revocation keeps this copy out after the account is restored.
        var raw = host.Raw();
        var signIn = await raw.PostAsync("/api/__test/signin/u-1", null);
        var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie")).Split(';')[0];
        HttpRequestMessage Chat() => new(HttpMethod.Post, "/api/chat") { Content = Json(), Headers = { { "Cookie", cookie } } };
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(Chat())).StatusCode);

        switch (change)
        {
            case "disabled": host.Users.Put("u-1", active: false); break;
            case "stamp": host.Users.Put("u-1", stamp: "S2"); break;
            case "role": host.Users.Put("u-1", role: BcRoles.Admin); break;
            case "deleted": host.Users.Users.TryRemove("u-1", out _); break;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(Chat())).StatusCode);

        host.Users.Put("u-1"); // restoring the account does not bring the old session back
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(Chat())).StatusCode);
    }

    [Fact]
    public async Task TheAccountIsCheckedOnEveryRequest()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");
        var before = host.Users.Reads;

        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync("/api/chat", Json());
        }

        Assert.Equal(before + 3, host.Users.Reads);
    }

    // ---------------------------------------------------------------- expiry

    [Fact]
    public async Task IdleFor30Minutes_EndsTheSession()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");

        host.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/chat", Json())).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/chat", Json())).StatusCode);
    }

    [Fact]
    public async Task ActiveUse_DoesNotExtendBeyond8Hours()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");

        for (var minutes = 20; minutes < 480; minutes += 20) // active every 20 minutes: never idle
        {
            host.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/chat", Json())).StatusCode);
        }

        host.Clock.Advance(TimeSpan.FromMinutes(20)); // 8 h after sign-in
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/chat", Json())).StatusCode);
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task UserStoreDown_IsRefused_NotAServerError_AndRecoversAfterwards()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var raw = host.Raw();
        var signIn = await raw.PostAsync("/api/__test/signin/u-1", null);
        var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie")).Split(';')[0];
        HttpRequestMessage Chat() => new(HttpMethod.Post, "/api/chat") { Content = Json(), Headers = { { "Cookie", cookie } } };

        host.Users.Unavailable = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(Chat())).StatusCode);

        host.Users.Unavailable = false; // an outage does not revoke the session
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(Chat())).StatusCode);
    }

    [Fact]
    public void EnabledWithoutAUserStore_RefusesToStart()
    {
        var host = SecurityHost.Create(factory, registerUserStore: false);

        // Refused while the host is built (dependency validation or the explicit startup check), never at the first request.
        var error = Assert.ThrowsAny<Exception>(() => host.Browser());

        Assert.Contains("IUserSecurityStateSource", error.ToString());
    }

    // ---------------------------------------------------------------- CSRF

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Origin", "https://evil.example")]
    public async Task CrossSitePost_WithAValidSession_Is403(string header, string value)
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json() };
        request.Headers.Add(header, value);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTH_CSRF_INVALID", await ErrorCode(response));
    }

    [Fact]
    public async Task SameOriginPost_FromTheBrowser_IsAllowed()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1");
        var client = host.Browser();
        await SignIn(client, "u-1");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json() };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "https://localhost");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    // ---------------------------------------------------------------- disabled (default)

    [Fact]
    public async Task Disabled_ChatStaysOpen_AsBefore_ButAdminOnlyStillNeedsSignIn()
    {
        var host = SecurityHost.Create(factory, enabled: false);
        var client = host.Browser();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/chat", Json())).StatusCode);
        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json(), Headers = { { "Sec-Fetch-Site", "cross-site" } } };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(crossSite)).StatusCode); // guard is off with authentication
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/__test/admin")).StatusCode);
    }

    // ---------------------------------------------------------------- audit log

    [Fact]
    public async Task AuditLog_RecordsEvents_WithoutSessionIdsCookiesOrStamps()
    {
        var host = SecurityHost.Create(factory);
        host.Users.Put("u-1", stamp: "STAMP-SECRET-123");
        var raw = host.Raw();
        var signIn = await raw.PostAsync("/api/__test/signin/u-1", null);
        var setCookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"));
        var cookieValue = setCookie.Split(';')[0].Split('=', 2)[1];
        host.Users.Put("u-1", active: false);
        await raw.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json(), Headers = { { "Cookie", setCookie.Split(';')[0] } } });

        var security = host.Logs.Entries.Where(e => e.EventId.Id is >= 5001 and <= 5007).ToList();
        Assert.Contains(security, e => e.EventId.Id == 5001);
        Assert.Contains(security, e => e.EventId.Id == 5003 && e.Message.Contains("UserDisabled"));
        foreach (var entry in host.Logs.Entries)
        {
            Check.NotLeaked(entry.Message, "STAMP-SECRET-123", cookieValue[..20]);
        }
    }
}
