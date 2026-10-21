using BC_LocalAI.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>QA-002 3.3 Session: expiry, revocation, cookie flags, fixation, tampering (manual clock, real login and cookie handling).</summary>
[Collection("Qa002")]
public sealed class Qa002SessionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<(QaHost Host, Acct Alice, Session Session)> Signed(string role = BcRoles.User)
    {
        var host = QaHost.Create(factory);
        var alice = host.AddUser("alice", role);
        return (host, alice, await host.Login(alice));
    }

    private static void Mutate(QaHost host, Acct account, Func<UserSecurityState, UserSecurityState> change) =>
        host.Users.Users[account.Id] = change(host.Users.Users[account.Id]);

    private static async Task ActiveFor(QaHost host, Session session, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(200, (await host.Get("/api/auth/me", session.Pair)).Code);
        }
    }

    [Fact]
    public async Task Idle_29m59s_StillValid_30m_Refused_AndStaysRefused()
    {
        var (host, _, session) = await Signed();
        host.Clock.Advance(TimeSpan.FromSeconds(29 * 60 + 59));
        var early = await host.Get("/api/auth/me", session.Pair);
        Qa.Verify("QA2-S-01-idle-29m", early.Code, early.ErrorCode);

        var (host2, _, session2) = await Signed();
        host2.Clock.Advance(TimeSpan.FromMinutes(30));
        host2.Ollama.Reset();
        var late = await host2.Chat(session2.Pair);
        host2.Clock.Advance(TimeSpan.FromMinutes(-29)); // even if the clock were set back, the session was revoked
        var after = await host2.Get("/api/auth/me", session2.Pair);
        Qa.Verify("QA2-S-02-idle-30m", late.Code, late.ErrorCode, host2.Ollama.Count, ok: after.Code == 401);
    }

    [Fact]
    public async Task Activity_ExtendsIdle_ButNeverTheEightHours()
    {
        var (host, _, session) = await Signed();
        await ActiveFor(host, session, 21); // 7 h
        var sevenHours = await host.Get("/api/auth/me", session.Pair);
        Qa.Verify("QA2-S-03-activity-extends-idle", sevenHours.Code, sevenHours.ErrorCode);

        await ActiveFor(host, session, 2); // 7 h 40
        host.Clock.Advance(TimeSpan.FromMinutes(19));
        var before = await host.Get("/api/auth/me", session.Pair);
        Qa.Verify("QA2-S-05-absolute-7h59", before.Code, before.ErrorCode);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        host.Ollama.Reset();
        var at = await host.Chat(session.Pair);
        Qa.Verify("QA2-S-04-absolute-8h", at.Code, at.ErrorCode, host.Ollama.Count);
    }

    [Fact]
    public async Task AccountDisabled_RefusesTheNextRequest_AndReenablingDoesNotRestore()
    {
        var (host, alice, session) = await Signed();
        Mutate(host, alice, u => u with { IsActive = false });
        host.Ollama.Reset();

        var chat = await host.Chat(session.Pair);
        Qa.Verify("QA2-S-06-disabled", chat.Code, chat.ErrorCode, host.Ollama.Count);

        Mutate(host, alice, u => u with { IsActive = true });
        var back = await host.Get("/api/auth/me", session.Pair);
        Qa.Verify("QA2-S-07-reenabled-stays-dead", back.Code, back.ErrorCode);
    }

    [Fact]
    public async Task RoleChange_Up_EndsTheSession_AndANewLoginGetsTheNewRole()
    {
        var (host, alice, session) = await Signed();
        Mutate(host, alice, u => u with { Role = BcRoles.Admin });

        var old = await host.Get("/api/auth/me", session.Pair);
        var fresh = await host.Login(alice);
        var admin = await host.Get("/api/__test/admin", fresh.Pair);

        Qa.Verify("QA2-S-08-role-up", old.Code, old.ErrorCode, ok: admin.Code == 200);
    }

    [Fact]
    public async Task RoleChange_Down_RemovesAdminAccessImmediately()
    {
        var (host, alice, session) = await Signed(BcRoles.Admin);
        Assert.Equal(200, (await host.Get("/api/__test/admin", session.Pair)).Code);
        Mutate(host, alice, u => u with { Role = BcRoles.User });

        var response = await host.Get("/api/__test/admin", session.Pair);

        Qa.Verify("QA2-S-09-role-down", response.Code, response.ErrorCode, ok: response.Code != 200);
    }

    [Theory]
    [InlineData("QA2-S-10-stamp")]
    [InlineData("QA2-S-11-deleted")]
    [InlineData("QA2-S-14-unknown-role")]
    public async Task OtherAccountChanges_EndTheSession(string id)
    {
        var (host, alice, session) = await Signed();
        switch (id)
        {
            case "QA2-S-10-stamp": Mutate(host, alice, u => u with { SecurityStamp = "ROTATED" }); break;
            case "QA2-S-11-deleted": host.Users.Users.TryRemove(alice.Id, out _); break;
            default: Mutate(host, alice, u => u with { Role = "KnowledgeManager" }); break;
        }

        var response = await host.Get("/api/auth/me", session.Pair);

        Qa.Verify(id, response.Code, response.ErrorCode);
    }

    [Fact]
    public async Task UserStoreDown_FailsClosed_AndRecovers()
    {
        var (host, _, session) = await Signed();
        host.Users.Unavailable = true;
        host.Ollama.Reset();

        var down = await host.Chat(session.Pair);
        host.Users.Unavailable = false;
        var back = await host.Get("/api/auth/me", session.Pair);

        Qa.Verify("QA2-S-12-store-down", down.Code, down.ErrorCode, host.Ollama.Count, ok: back.Code == 200);
    }

    [Fact]
    public async Task RevokeAll_EndsEverySessionOfTheUser_AndNobodyElses()
    {
        var host = QaHost.Create(factory);
        var alice = host.AddUser("alice");
        var bob = host.AddUser("bob");
        var phone = await host.Login(alice);
        var laptop = await host.Login(alice);
        var other = await host.Login(bob);

        Assert.Equal(200, (await host.Send(HttpMethod.Post, $"/api/__test/revoke-all/{alice.Id}")).Code);

        var a = await host.Get("/api/auth/me", phone.Pair);
        var b = await host.Get("/api/auth/me", laptop.Pair);
        var c = await host.Get("/api/auth/me", other.Pair);
        Qa.Verify("QA2-S-13-revoke-all", a.Code, a.ErrorCode, ok: b.Code == 401 && c.Code == 200);
    }

    [Fact]
    public async Task CookieFlags_AreStrict()
    {
        var host = QaHost.Create(factory);
        var alice = host.AddUser("alice");
        var csrf = await host.Get("/api/auth/csrf");
        var login = await host.LoginRaw(alice.Name, alice.Password);
        var cookie = login.SessionSetCookie!.ToLowerInvariant();
        var csrfCookie = csrf.SetCookies.Single(c => c.StartsWith("__Host-BC.Csrf=", StringComparison.Ordinal)).ToLowerInvariant();

        Qa.Verify("QA2-S-15-cookie-flags", ok: cookie.StartsWith("__host-bc.localai=") && cookie.Contains("secure") && cookie.Contains("httponly") &&
                                              cookie.Contains("samesite=lax") && cookie.Contains("path=/") && !cookie.Contains("domain="), detail: Flags(cookie));
        Qa.Verify("QA2-S-16-csrf-cookie-flags", ok: csrfCookie.Contains("secure") && csrfCookie.Contains("httponly") && csrfCookie.Contains("samesite=strict") &&
                                                      csrfCookie.Contains("path=/") && !csrfCookie.Contains("domain="), detail: Flags(csrfCookie));
        Qa.Verify("QA2-S-17-session-cookie", ok: !cookie.Contains("expires=") && !cookie.Contains("max-age="), detail: Flags(cookie));
    }

    /// <summary>Attribute names only, never the cookie value.</summary>
    private static string Flags(string setCookie) => string.Join("; ", setCookie.Split(';').Skip(1).Select(p => p.Trim().Split('=')[0]));

    [Fact]
    public async Task AuthenticatedRequests_DoNotReissueTheCookie()
    {
        var (host, _, session) = await Signed();
        host.Clock.Advance(TimeSpan.FromMinutes(10));

        var me = await host.Get("/api/auth/me", session.Pair);

        Qa.Verify("QA2-S-18-no-reissue", me.Code, ok: me.SessionSetCookie is null);
    }

    [Fact]
    public async Task Fixation_PlantedCookies_NeverBecomeASession()
    {
        var host = QaHost.Create(factory);
        var alice = host.AddUser("alice");
        var bob = host.AddUser("bob");
        var planted = BcAuthentication.CookieName + "=" + QaHost.NewSecret();

        var login = await host.LoginRaw(alice.Name, alice.Password, planted);
        var issued = login.SessionSetCookie!.Split(';', 2)[0];
        var viaPlanted = await host.Get("/api/auth/me", planted);
        var viaIssued = await host.Get("/api/auth/me", issued);
        Qa.Verify("QA2-S-19-fixation-garbage", viaPlanted.Code, viaPlanted.ErrorCode, ok: login.Code == 200 && issued != planted && viaIssued.Code == 200);

        // the attacker's own valid session planted in the victim's browser: the victim's login replaces it
        var attacker = await host.Login(alice);
        var victim = await host.Login(bob, previousCookie: attacker.Pair);
        var stale = await host.Get("/api/auth/me", attacker.Pair);
        var fresh = await host.Get("/api/auth/me", victim.Pair);
        Qa.Verify("QA2-S-20-fixation-valid-cookie", stale.Code, stale.ErrorCode, ok: fresh.Code == 200 && victim.Value != attacker.Value);
    }

    [Fact]
    public async Task TamperedCookies_Are401_NeverAServerError()
    {
        var (host, _, session) = await Signed();
        var value = session.Value;
        var flipped = value[..^4] + (value[^4] == 'A' ? 'B' : 'A') + value[^3..];
        var variants = new[]
        {
            BcAuthentication.CookieName + "=" + value[..(value.Length / 2)],
            BcAuthentication.CookieName + "=" + flipped,
            BcAuthentication.CookieName + "=",
            BcAuthentication.CookieName + "=" + new string('A', 10_000),
            BcAuthentication.CookieName + "=" + value + value,
            "x" + BcAuthentication.CookieName + "=" + value
        };
        host.Ollama.Reset();
        var codes = new List<int>();
        foreach (var cookie in variants)
        {
            codes.Add((await host.Get("/api/auth/me", cookie)).Code);
            codes.Add((await host.Chat(cookie)).Code);
        }

        var status = codes.All(c => c == 401) ? 401 : codes.First(c => c != 401);
        Qa.Verify("QA2-S-21-tampered-cookie", status, "AUTH_REQUIRED", host.Ollama.Count, detail: string.Join(",", codes));
    }

    [Fact]
    public async Task CookieValue_RevealsNothing()
    {
        var (host, alice, session) = await Signed();
        var value = Uri.UnescapeDataString(session.Value);
        var stamp = host.Users.Users[alice.Id].SecurityStamp;
        var sessionId = host.SessionIdOf(session);

        Qa.Verify("QA2-S-22-cookie-opaque", ok: !value.Contains(alice.Id, StringComparison.OrdinalIgnoreCase) && !value.Contains(stamp, StringComparison.OrdinalIgnoreCase) &&
                                                  !value.Contains(sessionId, StringComparison.Ordinal) && !value.Contains(alice.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoUsers_AreIsolated()
    {
        var host = QaHost.Create(factory);
        var alice = await host.Login(host.AddUser("alice"));
        var bob = await host.Login(host.AddUser("bob"));

        Assert.Equal(204, (await host.Logout(alice)).Code);
        var me = await host.Get("/api/auth/me", bob.Pair);

        Qa.Verify("QA2-S-23-two-users-isolated", me.Code, me.ErrorCode);
    }
}
