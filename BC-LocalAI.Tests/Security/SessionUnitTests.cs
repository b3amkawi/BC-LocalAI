using BC_LocalAI.Security;
using Microsoft.AspNetCore.Http;

namespace BC_LocalAI.Tests.Security;

public sealed class SessionValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly BcAuthenticationOptions Options = new(); // 30 min idle, 8 h absolute

    private static SessionRecord Session(DateTimeOffset? created = null, DateTimeOffset? lastSeen = null, string role = BcRoles.User, string stamp = "S1", DateTimeOffset? revoked = null) =>
        new("sid-1", "u-1", role, stamp, created ?? T0, lastSeen ?? created ?? T0, revoked);

    private static UserSecurityState User(bool active = true, DateTimeOffset? lockout = null, string role = BcRoles.User, string stamp = "S1", string id = "u-1") =>
        new(id, active, lockout, role, stamp);

    private static SessionRejectReason Reason(SessionRecord? s, UserSecurityState? u, DateTimeOffset now, string? cookieUser = "u-1") =>
        SessionValidator.Check(s, cookieUser, u, now, Options).Reason;

    [Fact]
    public void ValidSession_IsAccepted() =>
        Assert.True(SessionValidator.Check(Session(), "u-1", User(), T0.AddMinutes(5), Options).IsValid);

    [Fact]
    public void AdminSession_IsAccepted() =>
        Assert.True(SessionValidator.Check(Session(role: BcRoles.Admin), "u-1", User(role: BcRoles.Admin), T0, Options).IsValid);

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)] // exactly the idle timeout already ends the session
    [InlineData(31, false)]
    public void IdleTimeout_Boundary(int minutesIdle, bool valid)
    {
        var s = Session(lastSeen: T0);
        Assert.Equal(valid, SessionValidator.Check(s, "u-1", User(), T0.AddMinutes(minutesIdle), Options).IsValid);
        if (!valid) Assert.Equal(SessionRejectReason.IdleTimeoutExceeded, Reason(s, User(), T0.AddMinutes(minutesIdle)));
    }

    [Theory]
    [InlineData(479, true)]
    [InlineData(480, false)] // 8 hours after sign-in, however recently active
    [InlineData(600, false)]
    public void AbsoluteLifetime_Boundary_EvenWhenActive(int minutesSinceSignIn, bool valid)
    {
        var now = T0.AddMinutes(minutesSinceSignIn);
        var activeJustNow = Session(created: T0, lastSeen: now.AddMinutes(-1));
        Assert.Equal(valid, SessionValidator.Check(activeJustNow, "u-1", User(), now, Options).IsValid);
        if (!valid) Assert.Equal(SessionRejectReason.AbsoluteLifetimeExceeded, Reason(activeJustNow, User(), now));
    }

    [Fact]
    public void MissingSession_IsRefused() => Assert.Equal(SessionRejectReason.MissingSession, Reason(null, User(), T0));

    [Fact]
    public void RevokedSession_IsRefused() => Assert.Equal(SessionRejectReason.Revoked, Reason(Session(revoked: T0), User(), T0));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("u-2")]
    [InlineData("U-1")] // ids are compared exactly
    public void CookieForAnotherUsersSession_IsRefused(string? cookieUser) =>
        Assert.Equal(SessionRejectReason.UserMismatch, Reason(Session(), User(), T0, cookieUser));

    [Fact]
    public void DeletedUser_IsRefused() => Assert.Equal(SessionRejectReason.UserNotFound, Reason(Session(), null, T0));

    [Fact]
    public void StoreReturningAnotherUser_IsRefused() => Assert.Equal(SessionRejectReason.UserMismatch, Reason(Session(), User(id: "u-2"), T0));

    [Fact]
    public void DisabledUser_IsRefused() => Assert.Equal(SessionRejectReason.UserDisabled, Reason(Session(), User(active: false), T0));

    [Fact]
    public void LockedUser_IsRefused_UntilTheLockoutEnds()
    {
        Assert.Equal(SessionRejectReason.UserLocked, Reason(Session(), User(lockout: T0.AddMinutes(1)), T0));
        Assert.True(SessionValidator.Check(Session(), "u-1", User(lockout: T0.AddMinutes(-1)), T0, Options).IsValid);
    }

    [Fact]
    public void ChangedSecurityStamp_IsRefused() => Assert.Equal(SessionRejectReason.SecurityStampChanged, Reason(Session(stamp: "S1"), User(stamp: "S2"), T0));

    [Fact]
    public void ChangedRole_IsRefused_InBothDirections()
    {
        Assert.Equal(SessionRejectReason.RoleChanged, Reason(Session(role: BcRoles.User), User(role: BcRoles.Admin), T0));
        Assert.Equal(SessionRejectReason.RoleChanged, Reason(Session(role: BcRoles.Admin), User(role: BcRoles.User), T0));
    }

    [Theory]
    [InlineData("KnowledgeManager")]
    [InlineData("admin")]
    [InlineData("")]
    public void UnknownRole_IsRefused(string role) => Assert.Equal(SessionRejectReason.UnknownRole, Reason(Session(role: role), User(role: role), T0));
}

public sealed class BcAuthenticationOptionsTests
{
    private static bool Valid(BcAuthenticationOptions o) => new BcAuthenticationOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Defaults_AreTheSec001Decision_AndOff()
    {
        var o = new BcAuthenticationOptions();
        Assert.False(o.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(30), o.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(8), o.AbsoluteLifetime);
        Assert.True(Valid(o));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(241, 480)]
    [InlineData(30, 4)]
    [InlineData(30, 1441)]
    [InlineData(60, 30)] // idle longer than absolute makes no sense
    public void OutOfRangeValues_AreRefused(int idle, int absolute) =>
        Assert.False(Valid(new BcAuthenticationOptions { IdleTimeoutMinutes = idle, AbsoluteLifetimeMinutes = absolute }));

    [Fact]
    public void ShippedAppsettings_HasAuthenticationOff_AndTheDecidedValues()
    {
        var path = Path.Combine(TestPaths.WebProject(), "appsettings.json");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var section = json.RootElement.GetProperty("Authentication");
        Assert.False(section.GetProperty("Enabled").GetBoolean());
        Assert.Equal(30, section.GetProperty("IdleTimeoutMinutes").GetInt32());
        Assert.Equal(480, section.GetProperty("AbsoluteLifetimeMinutes").GetInt32());
    }
}

public sealed class InMemorySessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SessionIds_AreRandom256Bit_AndUnique()
    {
        var store = new InMemorySessionStore();
        var ids = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var s = await store.CreateAsync("u-1", BcRoles.User, "S1", T0);
            Assert.Equal(43, s.SessionId.Length); // 32 bytes, base64url without padding
            Assert.Matches("^[A-Za-z0-9_-]+$", s.SessionId);
            Assert.True(ids.Add(s.SessionId));
        }
    }

    [Fact]
    public async Task Touch_MovesLastSeen_ButNeverCreatedAt()
    {
        var store = new InMemorySessionStore();
        var s = await store.CreateAsync("u-1", BcRoles.User, "S1", T0);

        await store.TouchAsync(s.SessionId, T0.AddMinutes(10));
        await store.TouchAsync(s.SessionId, T0.AddMinutes(5)); // an older time never moves it back

        var after = await store.GetAsync(s.SessionId);
        Assert.Equal(T0, after!.CreatedAt);
        Assert.Equal(T0.AddMinutes(10), after.LastSeenAt);
    }

    [Fact]
    public async Task Revoke_IsFinal()
    {
        var store = new InMemorySessionStore();
        var s = await store.CreateAsync("u-1", BcRoles.User, "S1", T0);

        Assert.True(await store.RevokeAsync(s.SessionId, T0.AddMinutes(1)));
        Assert.False(await store.RevokeAsync(s.SessionId, T0.AddMinutes(2)));
        await store.TouchAsync(s.SessionId, T0.AddMinutes(3)); // touching a revoked session changes nothing
        var after = await store.GetAsync(s.SessionId);
        Assert.Equal(T0.AddMinutes(1), after!.RevokedAt);
        Assert.Equal(T0, after.LastSeenAt);
        Assert.False(await store.RevokeAsync("does-not-exist", T0));
    }

    [Fact]
    public async Task RevokeAllForUser_EndsOnlyThatUsersSessions()
    {
        var store = new InMemorySessionStore();
        var a1 = await store.CreateAsync("u-1", BcRoles.User, "S1", T0);
        var a2 = await store.CreateAsync("u-1", BcRoles.User, "S1", T0);
        var b = await store.CreateAsync("u-2", BcRoles.User, "S9", T0);

        Assert.Equal(2, await store.RevokeAllForUserAsync("u-1", T0));
        Assert.NotNull((await store.GetAsync(a1.SessionId))!.RevokedAt);
        Assert.NotNull((await store.GetAsync(a2.SessionId))!.RevokedAt);
        Assert.Null((await store.GetAsync(b.SessionId))!.RevokedAt);
        Assert.Equal(0, await store.RevokeAllForUserAsync("u-1", T0));
    }

    [Theory]
    [InlineData("KnowledgeManager")]
    [InlineData("")]
    public async Task UnknownRole_CannotGetASession(string role) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new InMemorySessionStore().CreateAsync("u-1", role, "S1", T0));

    [Fact]
    public async Task ConcurrentCreates_AllGetTheirOwnSession()
    {
        var store = new InMemorySessionStore();
        var sessions = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => store.CreateAsync($"u-{i}", BcRoles.User, "S", T0)));
        Assert.Equal(100, sessions.Select(s => s.SessionId).Distinct().Count());
    }
}

public sealed class SameSiteRequestGuardTests
{
    private static HttpRequest Request(string? fetchSite = null, string? origin = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("chat.local:5290");
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    [Theory]
    [InlineData(null, null, false)]                             // scripts, curl, tests: no browser headers
    [InlineData("same-origin", null, false)]
    [InlineData("none", null, false)]
    [InlineData("same-site", null, true)]                       // a sibling sub-domain is still another origin
    [InlineData("cross-site", null, true)]
    [InlineData(null, "https://chat.local:5290", false)]
    [InlineData(null, "https://CHAT.local:5290", false)]
    [InlineData(null, "https://evil.example", true)]
    [InlineData(null, "http://chat.local:5290", true)]          // scheme differs
    [InlineData(null, "https://chat.local:5291", true)]         // port differs
    [InlineData(null, "null", true)]                            // sandboxed or file:// pages
    [InlineData(null, "not a url", true)]
    [InlineData("same-origin", "https://evil.example", true)]   // either signal is enough to refuse
    public void CrossSiteDetection(string? fetchSite, string? origin, bool crossSite) =>
        Assert.Equal(crossSite, SameSiteRequestGuard.IsCrossSite(Request(fetchSite, origin)));

    [Theory]
    [InlineData("localhost:443", "https://localhost", false)]   // default port written on one side only
    [InlineData("localhost", "https://localhost:443", false)]
    [InlineData("localhost:8443", "https://localhost", true)]
    public void DefaultPortSpelling_IsComparedCanonically(string host, string origin, bool crossSite)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;
        Assert.Equal(crossSite, SameSiteRequestGuard.IsCrossSite(context.Request));
    }
}

internal static class TestPaths
{
    public static string WebProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BC-LocalAI", "appsettings.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("BC-LocalAI not found"), "BC-LocalAI");
    }
}
