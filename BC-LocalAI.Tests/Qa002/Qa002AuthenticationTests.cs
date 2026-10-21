using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BC_LocalAI.Security;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>
/// Stand-in for the MySQL tables behind BE-005: accounts with real IdentityV3 hashes (synthetic passwords) and an attempt store that
/// follows the same rules as the UPDATE statement in MySqlAuthenticationAttemptStore (count, lock at the threshold, only while the
/// stamp matches, the account is active and no lock is running). The SQL itself is covered by tests/BE005.MySqlTests (needs MySQL).
/// </summary>
internal sealed class SimAuthDb(QaHost host) : IAuthenticationRepository, IAuthenticationAttemptStore
{
    private sealed class Row
    {
        public required string Id, Username, DisplayName, PasswordHash, Role, SecurityStamp;
        public bool IsActive;
        public int FailedCount;
        public DateTimeOffset? LockoutEnd;
    }

    private readonly Dictionary<string, Row> _rows = [];
    private readonly object _gate = new();

    public bool Down { get; set; }
    public int Threshold { get; init; } = 5;
    public TimeSpan LockFor { get; init; } = TimeSpan.FromMinutes(15);

    public Acct Add(string name, string role = BcRoles.User, bool active = true)
    {
        var account = host.AddUser(name, role, active, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        lock (_gate)
        {
            _rows[name.ToUpperInvariant()] = new Row
            {
                Id = account.Id, Username = name, DisplayName = "ผู้ใช้สมมติ " + name, Role = role, IsActive = active,
                SecurityStamp = host.Users.Users[account.Id].SecurityStamp,
                PasswordHash = Sec001Fixtures.FixtureFactory.CreateHasher().HashPassword(name, account.Password)
            };
        }

        return account;
    }

    public string HashOf(string name) { lock (_gate) return _rows[name.ToUpperInvariant()].PasswordHash; }

    public void Lock(string name, TimeSpan from)
    {
        lock (_gate)
        {
            var row = _rows[name.ToUpperInvariant()];
            row.LockoutEnd = host.Clock.Now + from;
            host.Users.Users[row.Id] = host.Users.Users[row.Id] with { LockoutEnd = row.LockoutEnd };
        }
    }

    public Task<AuthenticationAccount?> FindAsync(string normalizedUsername, CancellationToken cancellationToken)
    {
        if (Down) throw new AuthenticationDependencyException();
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(normalizedUsername, out var r)
                ? new AuthenticationAccount
                {
                    Id = r.Id, Username = r.Username, DisplayName = r.DisplayName, PasswordHash = r.PasswordHash, IsActive = r.IsActive,
                    Role = r.Role, SecurityStamp = r.SecurityStamp, LockoutEndUtc = r.LockoutEnd
                }
                : null);
        }
    }

    public Task<bool> RecordAsync(string? userId, string? expectedStamp, bool passwordVerified, CancellationToken cancellationToken)
    {
        if (Down) throw new AuthenticationDependencyException();
        var now = host.Clock.Now;
        lock (_gate)
        {
            var row = _rows.Values.FirstOrDefault(r => r.Id == userId);
            if (row is null || row.SecurityStamp != expectedStamp || !row.IsActive || (row.LockoutEnd is not null && row.LockoutEnd > now))
                return Task.FromResult(false);
            if (passwordVerified) { row.FailedCount = 0; row.LockoutEnd = null; }
            else
            {
                row.FailedCount = row.LockoutEnd is not null ? 1 : Math.Min(row.FailedCount + 1, Threshold);
                row.LockoutEnd = row.FailedCount >= Threshold ? now + LockFor : null;
            }

            host.Users.Users[row.Id] = host.Users.Users[row.Id] with { LockoutEnd = row.LockoutEnd };
            return Task.FromResult(true);
        }
    }
}

/// <summary>QA-002 3.2 Authentication: the real login controller, limiter, verifier and MySqlAuthenticationBackend over a simulated database.</summary>
[Collection("Qa002")]
public sealed class Qa002AuthenticationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private (QaHost Host, SimAuthDb Db) Create() => CreateReal(factory);

    internal static (QaHost Host, SimAuthDb Db) CreateReal(WebApplicationFactory<Program> factory)
    {
        QaHost? created = null;
        SimAuthDb? db = null;
        var host = QaHost.Create(factory, services: s =>
        {
            s.RemoveAll<IAuthenticationBackend>();
            s.RemoveAll<IAuthenticationRepository>();
            s.RemoveAll<IAuthenticationAttemptStore>();
            s.AddSingleton<IAuthenticationRepository>(_ => db!);
            s.AddSingleton<IAuthenticationAttemptStore>(_ => db!);
            s.AddSingleton<IAuthenticationBackend, MySqlAuthenticationBackend>();
        });
        created = host;
        db = new SimAuthDb(created);
        return (host, db);
    }

    private static string Wrong(Acct account) => account.Password + "x";

    private static readonly string[] NoiseHeaders = ["Date", "Content-Length", "Server"];

    private static string[] HeaderNames(Resp response) =>
        response.Message.Headers.Select(h => h.Key).Concat(response.Message.Content.Headers.Select(h => h.Key))
            .Where(h => !NoiseHeaders.Contains(h, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public async Task Login_Succeeds_WithASafeBody()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");

        var response = await host.LoginRaw(alice.Name, alice.Password);

        var user = response.Json.GetProperty("user");
        var props = user.EnumerateObject().Select(p => p.Name).Order().ToArray();
        var forbidden = new[] { alice.Password, host.Users.Users[alice.Id].SecurityStamp, "hash", "stamp", "password", "AQAAAA" };
        Qa.Verify("QA2-A-01-login-ok", response.Code, response.ErrorCode,
            ok: props.SequenceEqual(["displayName", "id", "roles", "username"]) && response.Header("Cache-Control")!.Contains("no-store") &&
                response.IssuesSession && !forbidden.Any(f => response.Body.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task FailedLogins_AreIndistinguishable()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var disabled = db.Add("dora", active: false);
        var locked = db.Add("lena");
        db.Lock("lena", TimeSpan.FromMinutes(10));

        var wrong = await host.LoginRaw(alice.Name, Wrong(alice));
        var unknown = await host.LoginRaw("zed-unknown", QaHost.NewSecret());
        var off = await host.LoginRaw(disabled.Name, disabled.Password);
        var lockedOut = await host.LoginRaw(locked.Name, locked.Password);

        Qa.Verify("QA2-A-02-wrong-password", wrong.Code, wrong.ErrorCode);
        Qa.Verify("QA2-A-03-unknown-user", unknown.Code, unknown.ErrorCode, ok: unknown.Body == wrong.Body);
        Qa.Verify("QA2-A-04-disabled", off.Code, off.ErrorCode, ok: off.Body == wrong.Body);
        Qa.Verify("QA2-A-05-locked", lockedOut.Code, lockedOut.ErrorCode, ok: lockedOut.Body == wrong.Body);
        Qa.Verify("QA2-A-06-no-cookie-on-failure", ok: !new[] { wrong, unknown, off, lockedOut }.Any(r => r.IssuesSession));
        var sent = new[] { alice.Name, "zed-unknown", disabled.Name, locked.Name, alice.Password, disabled.Password, locked.Password };
        Qa.Verify("QA2-A-07-no-echo", ok: !new[] { wrong, unknown, off, lockedOut }.Any(r => sent.Any(s => r.Body.Contains(s, StringComparison.OrdinalIgnoreCase))));
        var reference = HeaderNames(wrong);
        Qa.Verify("QA2-A-08-same-headers", ok: new[] { unknown, off, lockedOut }.All(r => HeaderNames(r).SequenceEqual(reference)),
            detail: string.Join(",", reference));
    }

    [Fact]
    public async Task UnknownUser_DoesTheSameKeyDerivationWork()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        await host.LoginRaw("warmup-unknown", "warm");
        var known = new List<double>();
        var unknown = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            var timer = Stopwatch.StartNew();
            await host.LoginRaw("zed-unknown-" + i, "pw" + i);
            unknown.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            await host.LoginRaw(alice.Name, Wrong(alice));
            known.Add(timer.Elapsed.TotalMilliseconds);
            host.Clock.Advance(TimeSpan.FromMinutes(1)); // stay under the per-account rate limit
        }

        var ratio = Median(unknown) / Median(known);
        Qa.Verify("QA2-A-09-same-work", ok: ratio >= 0.5, detail: $"median unknown/known = {ratio:F2} (known {Median(known):F0} ms, unknown {Median(unknown):F0} ms)");
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    [Fact]
    public async Task Login_RequiresCsrfAndSameSite()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var (pair, token) = await host.Csrf();
        var body = new { username = alice.Name, password = alice.Password };

        var none = await host.Send(HttpMethod.Post, "/api/auth/login", pair, body);
        var forged = await host.Send(HttpMethod.Post, "/api/auth/login", pair, body, headers: [new("X-CSRF-TOKEN", "CfDJ8" + QaHost.NewSecret())]);
        var evil = await host.LoginRaw(alice.Name, alice.Password, csrf: (pair, token), headers: [new("Origin", "https://evil.example")]);
        var cross = await host.LoginRaw(alice.Name, alice.Password, csrf: (pair, token), headers: [new("Sec-Fetch-Site", "cross-site")]);
        var http = await host.LoginRaw(alice.Name, alice.Password, csrf: (pair, token), http: true);

        // a browser deletes the CSRF cookie when login succeeds; the old token then has no cookie to match
        var first = await host.LoginRaw(alice.Name, alice.Password, csrf: (pair, token));
        var replay = await host.Send(HttpMethod.Post, "/api/auth/login", first.SessionSetCookie!.Split(';', 2)[0], body, headers: [new("X-CSRF-TOKEN", token)]);

        Qa.Verify("QA2-A-10-no-csrf-token", none.Code, none.ErrorCode, host.Ollama.Count, ok: !none.IssuesSession);
        Qa.Verify("QA2-A-11-bad-csrf-token", forged.Code, forged.ErrorCode, host.Ollama.Count, ok: !forged.IssuesSession);
        Qa.Verify("QA2-A-13-cross-origin", evil.Code, evil.ErrorCode, host.Ollama.Count, ok: !evil.IssuesSession);
        Qa.Verify("QA2-A-14-cross-site-fetch-metadata", cross.Code, cross.ErrorCode, host.Ollama.Count, ok: !cross.IssuesSession);
        Qa.Verify("QA2-A-15-http", http.Code, http.ErrorCode, host.Ollama.Count, ok: !http.IssuesSession);
        Qa.Verify("QA2-A-12-old-csrf-token", replay.Code, replay.ErrorCode, host.Ollama.Count, ok: first.Code == 200 && !replay.IssuesSession);
    }

    [Theory]
    [InlineData("QA2-A-16-bad-input-not-json", "text/plain", """{"username":"alice","password":"x"}""")]
    [InlineData("QA2-A-17-bad-input-extra-role", "application/json", """{"username":"alice","password":"x","role":"Admin"}""")]
    [InlineData("QA2-A-18-bad-input-duplicate", "application/json", """{"username":"alice","username":"bob","password":"x"}""")]
    [InlineData("QA2-A-19-bad-input-wrong-type", "application/json", """{"username":123,"password":true}""")]
    [InlineData("QA2-A-20-bad-input-sql", "application/json", """{"username":"alice' OR 1=1 --","password":"x"}""")]
    [InlineData("QA2-A-21-bad-input-empty", "application/json", """{"username":"","password":""}""")]
    public async Task Login_RejectsBadInput(string id, string contentType, string body)
    {
        var (host, db) = Create();
        db.Add("alice");

        var response = await host.LoginRaw("", "", body: body, contentType: contentType);

        Qa.Verify(id, response.Code, response.ErrorCode, host.Ollama.Count, ok: !response.IssuesSession);
    }

    [Fact]
    public async Task Login_RejectsAnOversizedBody()
    {
        var (host, _) = Create();

        var response = await host.LoginRaw("", "", body: new string('x', 16 * 1024 + 1));

        Qa.Verify("QA2-A-22-oversized", response.Code, response.ErrorCode, host.Ollama.Count, ok: !response.IssuesSession);
    }

    [Fact]
    public async Task RateLimit_PerAccount_And_Reset()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var statuses = new List<int>();
        Resp last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await host.LoginRaw(alice.Name, Wrong(alice));
            statuses.Add(last.Code);
        }

        Qa.Verify("QA2-A-23-rate-limit-account", last.Code, last.ErrorCode,
            ok: statuses.Take(5).All(s => s == 401) && last.Header("Retry-After") is { Length: > 0 }, detail: string.Join(",", statuses));

        host.Clock.Advance(TimeSpan.FromSeconds(61));
        var next = await host.LoginRaw(alice.Name, Wrong(alice));
        Qa.Verify("QA2-A-24-rate-limit-reset", next.Code, next.ErrorCode);

        // the same limit for a name that does not exist: the 429 gives nothing away
        var ghost = new List<Resp>();
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        for (var i = 0; i < 6; i++) ghost.Add(await host.LoginRaw("zed-unknown", Wrong(alice)));
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        var known = new List<Resp>();
        for (var i = 0; i < 6; i++) known.Add(await host.LoginRaw("zoe", Wrong(alice)));
        Qa.Verify("QA2-A-25-rate-limit-same-body", ghost[5].Code, ghost[5].ErrorCode, ok: ghost[5].Body == last.Body && known[5].Body == last.Body);
    }

    [Fact]
    public async Task RateLimit_PerAddress()
    {
        var (host, _) = Create();
        var statuses = new List<int>();
        Resp last = null!;
        for (var i = 0; i < 31; i++)
        {
            last = await host.LoginRaw("zed-unknown-" + i, "pw");
            statuses.Add(last.Code);
        }

        Qa.Verify("QA2-A-26-rate-limit-ip", last.Code, last.ErrorCode, ok: statuses.Take(30).All(s => s == 401), detail: string.Join(",", statuses.TakeLast(3)));
    }

    [Fact]
    public async Task Lockout_AfterFiveWrongPasswords_ThenExpires()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        Resp reference = null!;
        for (var i = 0; i < 5; i++)
        {
            reference = await host.LoginRaw(alice.Name, Wrong(alice));
            host.Clock.Advance(TimeSpan.FromSeconds(61)); // outside the 5-per-minute limit: only the lockout can refuse
        }

        var correct = await host.LoginRaw(alice.Name, alice.Password);
        Qa.Verify("QA2-A-27-lockout", correct.Code, correct.ErrorCode, ok: correct.Body == reference.Body && !correct.IssuesSession);

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        var afterwards = await host.LoginRaw(alice.Name, alice.Password);
        Qa.Verify("QA2-A-28-lockout-expires", afterwards.Code, afterwards.ErrorCode, ok: afterwards.IssuesSession);
    }

    [Fact]
    public async Task Lockout_DoesNotEndTheOwnersSession()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var session = await host.Login(alice);
        for (var i = 0; i < 5; i++)
        {
            await host.LoginRaw(alice.Name, Wrong(alice));
            host.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.Equal(401, (await host.LoginRaw(alice.Name, alice.Password)).Code); // locked for new sign-ins
        var me = await host.Get("/api/auth/me", session.Pair);

        Qa.Verify("QA2-A-29-lockout-keeps-session", me.Code, me.ErrorCode);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsTheFailureCount()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        async Task Fail(int n)
        {
            for (var i = 0; i < n; i++)
            {
                await host.LoginRaw(alice.Name, Wrong(alice));
                host.Clock.Advance(TimeSpan.FromSeconds(61));
            }
        }

        await Fail(4);
        Assert.Equal(200, (await host.LoginRaw(alice.Name, alice.Password)).Code);
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        await Fail(4);
        var final = await host.LoginRaw(alice.Name, alice.Password);

        Qa.Verify("QA2-A-30-success-resets", final.Code, final.ErrorCode);
    }

    [Fact]
    public async Task Username_IsCaseInsensitive_Password_IsExact()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");

        var upper = await host.LoginRaw("ALICE", alice.Password);
        var spaced = await host.LoginRaw(alice.Name, alice.Password + " ");

        Qa.Verify("QA2-A-31-username-case", upper.Code, upper.ErrorCode, ok: upper.IssuesSession);
        Qa.Verify("QA2-A-32-password-exact", spaced.Code, spaced.ErrorCode, ok: !spaced.IssuesSession);
    }

    [Fact]
    public async Task DatabaseDown_Is503_WithoutDetail()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        db.Down = true;

        var response = await host.LoginRaw(alice.Name, alice.Password);

        Qa.Verify("QA2-A-33-db-down", response.Code, response.ErrorCode,
            ok: !response.IssuesSession && !response.Body.Contains("MySql", StringComparison.OrdinalIgnoreCase) && !response.Body.Contains("Exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_EndsTheSession_AndIsIdempotent()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var session = await host.Login(alice);

        var first = await host.Logout(session);
        var second = await host.Logout(session);
        host.Ollama.Reset();
        var me = await host.Get("/api/auth/me", session.Pair);
        var chat = await host.Chat(session.Pair);

        var cleared = first.SetCookies.Any(c => c.StartsWith(BcAuthentication.CookieName + "=;", StringComparison.Ordinal));
        Qa.Verify("QA2-A-34-logout", first.Code, first.ErrorCode, host.Ollama.Count, ok: cleared && me.Code == 401 && chat.Code == 401);
        Qa.Verify("QA2-A-35-logout-twice", second.Code, second.ErrorCode, ok: first.Code == 204);
    }

    [Fact]
    public async Task Logout_WithoutCsrfToken_IsRefused_AndSessionSurvives()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var session = await host.Login(alice);

        var response = await host.Logout(session, withToken: false);
        var me = await host.Get("/api/auth/me", session.Pair);

        Qa.Verify("QA2-A-36-logout-no-csrf", response.Code, response.ErrorCode, ok: me.Code == 200);
    }

    [Fact]
    public async Task Me_ReturnsOnlyThePublicProjection()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var session = await host.Login(alice);

        var me = await host.Get("/api/auth/me", session.Pair);

        var props = me.Json.GetProperty("user").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Qa.Verify("QA2-A-37-me-shape", me.Code, me.ErrorCode, ok: props.SequenceEqual(["displayName", "id", "roles", "username"]) && me.Json.EnumerateObject().Count() == 1);
    }

    [Fact]
    public async Task Relogin_OnTheSameBrowser_RevokesTheOldSession()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");
        var bob = db.Add("bob");
        var first = await host.Login(alice);

        var second = await host.Login(bob, previousCookie: first.Pair);
        var old = await host.Get("/api/auth/me", first.Pair);
        var current = await host.Get("/api/auth/me", second.Pair);

        Qa.Verify("QA2-A-38-relogin", old.Code, old.ErrorCode, ok: current.Code == 200 && second.Value != first.Value);
    }

    [Fact]
    public async Task ParallelLogins_NeverFailWith500()
    {
        var (host, db) = Create();
        var alice = db.Add("alice");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? host.LoginRaw(alice.Name, alice.Password)
            : host.LoginRaw("zed-unknown-" + i, "pw")));

        var codes = results.Select(r => r.Code).ToArray();
        Qa.Verify("QA2-A-39-concurrent-login-cap", ok: codes.All(c => c is 200 or 401 or 429), detail: string.Join(",", codes));
    }
}
