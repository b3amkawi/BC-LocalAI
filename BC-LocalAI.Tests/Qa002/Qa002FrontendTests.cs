using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>
/// QA-002 follow-up for FE-003 / SEC-003 (main gained them after the first run): the X-BC-Expected-User precondition, the CSRF header
/// required by cookie-backed history mutations, the HTTPS guard, and the real login page. The harness sends the CSRF token itself in the
/// other test classes; here AutoCsrf is off so each request carries exactly the headers the case describes.
/// </summary>
[Collection("Qa002")]
public sealed class Qa002FrontendTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record World(QaHost Host, Acct Alice, Acct Bob, Session A, Session B, string ConversationId);

    private async Task<World> Create(IDictionary<string, string?>? settings = null, string environment = "Production")
    {
        var host = QaHost.Create(factory, history: true, environment: environment, settings: settings);
        var alice = host.AddUser("alice");
        var bob = host.AddUser("bob");
        var a = await host.Login(alice);
        var b = await host.Login(bob);
        var created = await host.Browser(HttpMethod.Post, "/api/conversations", a.Pair, new { title = "original" }); // AutoCsrf supplies the token here
        host.AutoCsrf = false;
        return new World(host, alice, bob, a, b, created.Json.GetProperty("id").GetString()!);
    }

    private static KeyValuePair<string, string> H(string name, string value) => new(name, value);

    private static IEnumerable<KeyValuePair<string, string>> WithBrowser(params KeyValuePair<string, string>[] extra) =>
        QaHost.BrowserHeaders.Concat(extra);

    private static Task<Resp> Expect(World w, HttpMethod method, string path, string cookie, object? json, params string[] expected) =>
        w.Host.Send(method, path, cookie, json, headers: WithBrowser(expected.Select(e => H("X-BC-Expected-User", e)).ToArray()));

    /// <summary>The conversation is as it was and nothing was written or sent to the model.</summary>
    private static async Task<bool> Untouched(World w)
    {
        var conversation = await w.Host.Get($"/api/conversations/{w.ConversationId}", w.A.Pair);
        var list = await w.Host.Get("/api/conversations", w.A.Pair);
        var messages = await w.Host.Get($"/api/conversations/{w.ConversationId}/messages", w.A.Pair);
        return conversation.Code == 200 && conversation.Json.GetProperty("title").GetString() == "original" &&
               list.Json.GetProperty("items").GetArrayLength() == 1 && messages.Json.GetProperty("items").GetArrayLength() == 0;
    }

    // ------------------------------------------------------------------ X-BC-Expected-User
    [Fact]
    public async Task ExpectedUser_Match_And_Mismatch()
    {
        var w = await Create();

        var match = await Expect(w, HttpMethod.Get, "/api/auth/me", w.A.Pair, null, w.Alice.Id);
        Qa.Verify("QA2-F-01-expected-match", match.Code, match.ErrorCode,
            ok: match.Header("X-BC-User") == w.Alice.Id && match.Header("Cache-Control")!.Contains("no-store"));

        var wrong = await Expect(w, HttpMethod.Get, "/api/auth/me", w.A.Pair, null, w.Bob.Id);
        var after = await w.Host.Get("/api/auth/me", w.A.Pair);
        Qa.Verify("QA2-F-02-expected-mismatch", wrong.Code, wrong.ErrorCode, ok: after.Code == 200 && wrong.Header("X-BC-User") is null);

        w.Host.Ollama.Reset();
        var chat = await Expect(w, HttpMethod.Post, "/api/chat", w.A.Pair, new { message = "x" }, w.Bob.Id);
        Qa.Verify("QA2-F-03-mismatch-chat", chat.Code, chat.ErrorCode, w.Host.Ollama.Count);

        var stream = await Expect(w, HttpMethod.Post, "/api/chat/stream", w.A.Pair, new { message = "x" }, w.Bob.Id);
        Qa.Verify("QA2-F-04-mismatch-stream", stream.Code, stream.ErrorCode, w.Host.Ollama.Count,
            ok: stream.Header("Content-Type")!.StartsWith("application/json", StringComparison.Ordinal));

        var (pair, token) = await w.Host.Csrf(w.A.Pair);
        var read = await Expect(w, HttpMethod.Get, "/api/conversations", w.A.Pair, null, w.Bob.Id);
        var create = await w.Host.Send(HttpMethod.Post, "/api/conversations", w.A.Pair + "; " + pair, new { title = "made under the wrong account" },
            headers: WithBrowser(H("X-CSRF-TOKEN", token), H("X-BC-Expected-User", w.Bob.Id)));
        Qa.Verify("QA2-F-05-mismatch-conversations", read.Code, read.ErrorCode, ok: create.Code == 401 && create.ErrorCode == "AUTH_SESSION_CHANGED" && await Untouched(w));
    }

    [Fact]
    public async Task ExpectedUser_MalformedForms_AreMismatches()
    {
        var w = await Create();
        Assert.NotEqual(w.Alice.Id, w.Alice.Id.ToUpperInvariant());

        var twice = await Expect(w, HttpMethod.Get, "/api/auth/me", w.A.Pair, null, w.Alice.Id, w.Alice.Id);
        Qa.Verify("QA2-F-06-duplicate-header", twice.Code, twice.ErrorCode);

        var empty = await Expect(w, HttpMethod.Get, "/api/auth/me", w.A.Pair, null, "");
        // ASP.NET's TestServer never delivers an empty header value to the application (probed: the header is absent), so the
        // product's handling of an empty value (Count != 1 -> mismatch) cannot be exercised here: recorded as an observation, not a pass.
        Qa.Observe("QA2-F-07-empty-header", empty.Code, empty.ErrorCode, detail: "not testable with TestServer: the empty header never reaches the application; code review only (expected.Count != 1 is a mismatch)");

        var upper = await Expect(w, HttpMethod.Get, "/api/auth/me", w.A.Pair, null, w.Alice.Id.ToUpperInvariant());
        Qa.Verify("QA2-F-08-case-sensitive", upper.Code, upper.ErrorCode);
    }

    private static readonly (HttpMethod Method, string Path)[] Aliases =
    [
        (HttpMethod.Post, "/API/CHAT"), (HttpMethod.Post, "/api/chat/"), (HttpMethod.Post, "/Api/Conversations/"),
        (HttpMethod.Post, "/api/CHAT/STREAM/"), (HttpMethod.Get, "/api/auth/ME")
    ];

    [Fact]
    public async Task PathAliases_AreCoveredByThePreconditionAndByAuthentication()
    {
        var w = await Create();
        w.Host.Ollama.Reset();
        var mismatches = new List<Resp>();
        var anonymous = new List<Resp>();
        foreach (var (method, path) in Aliases)
        {
            var body = method == HttpMethod.Post ? (path.Contains("onversations", StringComparison.OrdinalIgnoreCase) ? new { title = "x" } : (object)new { message = "x" }) : null;
            mismatches.Add(await Expect(w, method, path, w.A.Pair, body, w.Bob.Id));
            anonymous.Add(await w.Host.Send(method, path, null, body, headers: QaHost.BrowserHeaders));
        }

        var badMismatch = mismatches.FirstOrDefault(r => r.Code != 401 || r.ErrorCode != "AUTH_SESSION_CHANGED");
        Qa.Verify("QA2-F-09-alias-mismatch", badMismatch?.Code ?? 401, badMismatch?.ErrorCode ?? "AUTH_SESSION_CHANGED", w.Host.Ollama.Count,
            detail: string.Join(",", mismatches.Select(r => r.Code)));
        var badAnonymous = anonymous.FirstOrDefault(r => r.Code != 401 || r.ErrorCode != "AUTH_REQUIRED");
        Qa.Verify("QA2-F-10-alias-anonymous", badAnonymous?.Code ?? 401, badAnonymous?.ErrorCode ?? "AUTH_REQUIRED", w.Host.Ollama.Count,
            detail: string.Join(",", anonymous.Select(r => r.Code)));
    }

    [Fact]
    public async Task PathAliases_DoNotBypassTheCsrfToken()
    {
        var w = await Create();

        var upper = await w.Host.Send(HttpMethod.Post, "/API/CONVERSATIONS", w.A.Pair, new { title = "forged" }, headers: QaHost.BrowserHeaders);
        var slash = await w.Host.Send(HttpMethod.Post, "/api/conversations/", w.A.Pair, new { title = "forged" }, headers: QaHost.BrowserHeaders);

        Qa.Verify("QA2-F-11-alias-no-csrf", upper.Code, upper.ErrorCode, ok: slash.Code == 403 && slash.ErrorCode == "AUTH_CSRF_INVALID" && await Untouched(w),
            detail: $"slash alias {slash.Code} {slash.ErrorCode}");
    }

    [Fact]
    public async Task ReSignedInBrowser_OldTabIsToldTheAccountChanged()
    {
        var host = QaHost.Create(factory, history: true);
        var alice = host.AddUser("alice");
        var bob = host.AddUser("bob");
        var a = await host.Login(alice);
        var b = await host.Login(bob, previousCookie: a.Pair); // same browser, another user signs in over the first session
        host.Ollama.Reset();

        var staleTab = await host.Send(HttpMethod.Post, "/api/chat", b.Pair, new { message = "x" }, headers: WithBrowser(H("X-BC-Expected-User", alice.Id)));
        var me = await host.Get("/api/auth/me", b.Pair);

        Qa.Verify("QA2-F-34-relogin-other-user", staleTab.Code, staleTab.ErrorCode, host.Ollama.Count, ok: me.Code == 200 && me.Header("X-BC-User") == bob.Id);
    }

    [Fact]
    public async Task AfterLogout_TheAnswerIsAuthRequired_NotSessionChanged()
    {
        var w = await Create();
        Assert.Equal(204, (await w.Host.Logout(w.A)).Code);
        w.Host.Ollama.Reset();

        var response = await Expect(w, HttpMethod.Post, "/api/chat", w.A.Pair, new { message = "x" }, w.Alice.Id);

        Qa.Verify("QA2-F-35-after-logout", response.Code, response.ErrorCode, w.Host.Ollama.Count, ok: response.Header("X-BC-User") is null);
    }

    [Fact]
    public async Task AuthenticationOff_IgnoresTheFrontendHeaders()
    {
        var host = QaHost.Create(factory, authentication: false);

        var response = await host.Send(HttpMethod.Post, "/api/chat", json: new { message = "x" },
            headers: WithBrowser(H("X-BC-Expected-User", Guid.NewGuid().ToString()), H("X-CSRF-TOKEN", "CfDJ8garbage")));

        Qa.Verify("QA2-F-37-auth-off-header-ignored", response.Code, response.ErrorCode, host.Ollama.Count, ok: response.Header("X-BC-User") is null);
    }

    // ------------------------------------------------------------------ HTTPS guard
    [Fact]
    public async Task Http_IsRefusedForHistory_WithAndWithoutAConfiguredHttpsPort()
    {
        var results = new List<string>();
        Resp? first = null;
        var calls = 0;
        foreach (var settings in new Dictionary<string, string?>?[] { null, new Dictionary<string, string?> { ["https_port"] = "443" } })
        {
            var w = await Create(settings);
            w.Host.Ollama.Reset();
            var conv = w.ConversationId;
            var attempts = new[]
            {
                await w.Host.Send(HttpMethod.Post, "/api/conversations", w.A.Pair, new { title = "over http" }, http: true),
                await w.Host.Send(HttpMethod.Get, "/api/conversations", w.A.Pair, http: true),
                await w.Host.Send(HttpMethod.Post, "/api/chat", w.A.Pair, new { message = "x", conversationId = conv }, http: true),
                await w.Host.Send(HttpMethod.Post, "/api/chat/stream", w.A.Pair, new { message = "x", conversationId = conv }, http: true)
            };
            foreach (var r in attempts)
            {
                calls += w.Host.Ollama.Count;
                results.Add($"{r.Code}/{r.ErrorCode}");
                if (first is null && (r.Code != 403 || r.ErrorCode != "AUTH_HTTPS_REQUIRED")) first = r;
            }
        }

        Qa.Verify("QA2-F-12-http-history", first?.Code ?? 403, first?.ErrorCode ?? "AUTH_HTTPS_REQUIRED", calls == 0 ? 0 : calls,
            detail: "without port, then with port: " + string.Join(" ", results));
    }

    [Fact]
    public async Task Http_WithTheExpectedUserHeader_IsRefused()
    {
        var codes = new List<string>();
        Resp? first = null;
        var calls = 0;
        foreach (var settings in new Dictionary<string, string?>?[] { null, new Dictionary<string, string?> { ["https_port"] = "443" } })
        {
            var w = await Create(settings);
            w.Host.Ollama.Reset();
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[] { (HttpMethod.Post, "/api/chat", new { message = "x" }), (HttpMethod.Get, "/api/auth/me", null) })
            {
                var r = await w.Host.Send(method, path, w.A.Pair, body, headers: [H("X-BC-Expected-User", w.Alice.Id)], http: true);
                codes.Add($"{r.Code}/{r.ErrorCode}");
                if (first is null && (r.Code != 403 || r.ErrorCode != "AUTH_HTTPS_REQUIRED")) first = r;
            }

            calls += w.Host.Ollama.Count;
        }

        Qa.Verify("QA2-F-13-http-expected-header", first?.Code ?? 403, first?.ErrorCode ?? "AUTH_HTTPS_REQUIRED", calls, detail: string.Join(" ", codes));
    }

    // ------------------------------------------------------------------ CSRF token on history mutations
    [Fact]
    public async Task LegacyChat_WithoutAConversation_IsUnchanged()
    {
        var w = await Create();
        w.Host.Ollama.Reset();

        var chat = await w.Host.Send(HttpMethod.Post, "/api/chat", w.A.Pair, new { message = "legacy" }, headers: QaHost.BrowserHeaders);
        var (head, events) = await w.Host.Stream(w.A.Pair, "legacy");

        Qa.Verify("QA2-F-14-legacy-unchanged", chat.Code, chat.ErrorCode, w.Host.Ollama.Count, ok: head.Code == 200 && events.Last().Name == "completed");
    }

    private static readonly string[] Ops = ["create", "rename", "delete", "chat", "stream"];

    private static Task<Resp> Mutate(World w, string op, string cookie, params KeyValuePair<string, string>[] tokenHeaders)
    {
        var headers = WithBrowser(tokenHeaders);
        var id = w.ConversationId;
        return op switch
        {
            "create" => w.Host.Send(HttpMethod.Post, "/api/conversations", cookie, new { title = "must not exist" }, headers: headers),
            "rename" => w.Host.Send(HttpMethod.Patch, $"/api/conversations/{id}", cookie, new { title = "must not change" }, headers: headers),
            "delete" => w.Host.Send(HttpMethod.Delete, $"/api/conversations/{id}", cookie, headers: headers),
            "chat" => w.Host.Send(HttpMethod.Post, "/api/chat", cookie, new { message = "never sent", conversationId = id }, headers: headers),
            _ => w.Host.Send(HttpMethod.Post, "/api/chat/stream", cookie, new { message = "never sent", conversationId = id }, headers: headers)
        };
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task HistoryMutation_WithoutOrWithDuplicateToken_IsRefused(string op)
    {
        var w = await Create();
        var (pair, token) = await w.Host.Csrf(w.A.Pair);
        w.Host.Ollama.Reset();

        var missing = await Mutate(w, op, w.A.Pair + "; " + pair);
        Qa.Verify($"QA2-F-15-missing-token-{op}", missing.Code, missing.ErrorCode, w.Host.Ollama.Count, ok: await Untouched(w));

        var twice = await Mutate(w, op, w.A.Pair + "; " + pair, H("X-CSRF-TOKEN", token), H("X-CSRF-TOKEN", token));
        Qa.Verify($"QA2-F-16-duplicate-token-{op}", twice.Code, twice.ErrorCode, w.Host.Ollama.Count, ok: await Untouched(w));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("other-user")]
    [InlineData("anonymous")]
    public async Task HistoryMutation_WithABadToken_IsRefused(string kind)
    {
        var w = await Create();
        string pair, token;
        switch (kind)
        {
            case "garbage": (pair, token) = (( await w.Host.Csrf(w.A.Pair)).CookiePair, "CfDJ8" + QaHost.NewSecret() + QaHost.NewSecret()); break;
            case "other-user": (pair, token) = await w.Host.Csrf(w.B.Pair); break;
            default: (pair, token) = await w.Host.Csrf(); break;
        }

        w.Host.Ollama.Reset();
        var create = await Mutate(w, "create", w.A.Pair + "; " + pair, H("X-CSRF-TOKEN", token));
        var chat = await Mutate(w, "chat", w.A.Pair + "; " + pair, H("X-CSRF-TOKEN", token));

        Qa.Verify($"QA2-F-17-bad-token-{kind}", create.Code, create.ErrorCode, w.Host.Ollama.Count, ok: chat.Code == 403 && chat.ErrorCode == "AUTH_CSRF_INVALID" && await Untouched(w));
    }

    [Fact]
    public async Task HistoryMutation_WithAValidToken_Works()
    {
        var w = await Create();
        var (pair, token) = await w.Host.Csrf(w.A.Pair);
        var cookie = w.A.Pair + "; " + pair;
        w.Host.Ollama.Reset();
        var token1 = H("X-CSRF-TOKEN", token);

        var create = await Mutate(w, "create", cookie, token1);
        var rename = await Mutate(w, "rename", cookie, token1);
        var chat = await Mutate(w, "chat", cookie, token1);
        var stream = await Mutate(w, "stream", cookie, token1);
        var delete = await Mutate(w, "delete", cookie, token1);

        var statuses = new[] { create.Code, rename.Code, chat.Code, stream.Code, delete.Code };
        var expected = new[] { 201, 200, 200, 200, 204 };
        var firstBad = Array.FindIndex(statuses, (s) => false);
        var bad = statuses.Zip(expected).Select((p, i) => (p, i)).FirstOrDefault(x => x.p.First != x.p.Second);
        Qa.Verify("QA2-F-18-valid-token", bad.p == default ? 200 : bad.p.First, null, w.Host.Ollama.Count,
            ok: QaHost.Parse(stream.Body).LastOrDefault()?.Name == "completed", detail: string.Join(",", statuses) + (firstBad < 0 ? "" : ""));
    }

    [Fact]
    public async Task UnknownConversation_WithoutToken_DoesNotRevealWhetherItExists()
    {
        var w = await Create();
        var (pair, _) = await w.Host.Csrf(w.A.Pair);
        w.Host.Ollama.Reset();

        var chat = await w.Host.Send(HttpMethod.Post, "/api/chat", w.A.Pair + "; " + pair, new { message = "x", conversationId = Guid.NewGuid().ToString() }, headers: QaHost.BrowserHeaders);
        var stream = await w.Host.Send(HttpMethod.Post, "/api/chat/stream", w.A.Pair + "; " + pair, new { message = "x", conversationId = Guid.NewGuid().ToString() }, headers: QaHost.BrowserHeaders);

        Qa.Verify("QA2-F-20-unknown-conversation-no-token", chat.Code, chat.ErrorCode, w.Host.Ollama.Count, ok: stream.Code == 403 && stream.ErrorCode == "AUTH_CSRF_INVALID");
    }

    // ------------------------------------------------------------------ headers on the API
    [Fact]
    public async Task AuthenticatedApi_IsNoStore_AndNamesTheCaller()
    {
        var w = await Create();
        w.Host.AutoCsrf = true;
        var responses = new (string Name, Resp Response)[]
        {
            ("me", await w.Host.Get("/api/auth/me", w.A.Pair)),
            ("chat", await w.Host.Chat(w.A.Pair)),
            ("stream", (await w.Host.Stream(w.A.Pair)).Head),
            ("list", await w.Host.Get("/api/conversations", w.A.Pair)),
            ("create", await w.Host.Browser(HttpMethod.Post, "/api/conversations", w.A.Pair, new { title = "t" })),
            ("get-404", await w.Host.Get($"/api/conversations/{Guid.NewGuid()}", w.A.Pair))
        };

        var missing = responses.Where(r => !(r.Response.Header("Cache-Control") ?? "").Contains("no-store") || r.Response.Header("X-BC-User") != w.Alice.Id).Select(r => r.Name).ToArray();
        Qa.Verify("QA2-F-21-no-store-actor", responses[0].Response.Code, ok: missing.Length == 0, detail: "statuses " + string.Join(",", responses.Select(r => r.Response.Code)) + "; missing: " + string.Join(",", missing));

        var anonymous = new[]
        {
            await w.Host.Chat(null), await w.Host.Get("/api/auth/me"), await w.Host.Get("/api/conversations"), await w.Host.Chat(null, stream: true)
        };
        Qa.Verify("QA2-F-22-anonymous-no-actor", anonymous[0].Code, anonymous[0].ErrorCode,
            ok: anonymous.All(r => r.Code == 401 && r.Header("X-BC-User") is null && (r.Header("Cache-Control") ?? "").Contains("no-store")));
    }

    // ------------------------------------------------------------------ pages
    [Fact]
    public async Task LoginPage_Rules()
    {
        var off = QaHost.Create(factory, authentication: false);
        var disabled = await off.Get("/login");
        Qa.Verify("QA2-F-23-login-auth-off", disabled.Code);

        var host = QaHost.Create(factory, history: true);
        var alice = host.AddUser("alice");
        var http = await host.Send(HttpMethod.Get, "/login", http: true);
        Qa.Verify("QA2-F-24-login-http", http.Code);

        var page = await host.Get("/login?returnUrl=https://untrusted.invalid");
        var csp = page.Header("Content-Security-Policy") ?? "";
        Qa.Verify("QA2-F-25-login-page", page.Code, ok: (page.Header("Cache-Control") ?? "").Contains("no-store") && csp.Contains("form-action 'none'") && csp.Contains("frame-ancestors 'none'") &&
                                                      page.Header("X-Content-Type-Options") == "nosniff" && page.Body.Contains("type=\"password\"") &&
                                                      !page.Body.Contains("untrusted.invalid") && !page.Body.Contains("auth-mock") && !page.Body.Contains("auth-preview"));

        var session = await host.Login(alice);
        var signedIn = await host.Get("/login?returnUrl=https://untrusted.invalid", session.Pair);
        Qa.Verify("QA2-F-26-login-signed-in", signedIn.Code, ok: signedIn.Message.Headers.Location?.OriginalString == "/");
    }

    [Fact]
    public async Task RootPage_Rules()
    {
        var host = QaHost.Create(factory, history: true);
        var alice = host.AddUser("alice");
        var anonymous = await host.Get("/");
        Qa.Verify("QA2-F-27-root-anonymous", anonymous.Code, ok: anonymous.Message.Headers.Location?.OriginalString == "/login" && (anonymous.Header("Cache-Control") ?? "").Contains("no-store"));

        var session = await host.Login(alice);
        var http = await host.Send(HttpMethod.Get, "/", session.Pair, http: true);
        Qa.Verify("QA2-F-28-root-http", http.Code, detail: "without a configured HTTPS port; with one the middleware answers 307 to HTTPS first");

        var page = await host.Get("/", session.Pair);
        var forbidden = new[] { alice.Id, host.Users.Users[alice.Id].SecurityStamp, session.Value, Uri.UnescapeDataString(session.Value), "CfDJ8" };
        Qa.Verify("QA2-F-30-root-no-leak", page.Code, ok: (page.Header("Cache-Control") ?? "").Contains("no-store") && !forbidden.Any(f => page.Body.Contains(f, StringComparison.Ordinal)));

        host.Users.Users[alice.Id] = host.Users.Users[alice.Id] with { IsActive = false };
        var disabled = await host.Get("/", session.Pair);
        Qa.Verify("QA2-F-29-root-disabled-account", disabled.Code, ok: disabled.Message.Headers.Location?.OriginalString == "/login");
    }

    [Fact]
    public async Task Preview_IsNotExposed()
    {
        var results = new List<string>();
        foreach (var (environment, enabled) in new[] { ("Production", "true"), ("Development", "false"), ("Development", "true") })
        {
            var host = QaHost.Create(factory, environment: environment, settings: new Dictionary<string, string?> { ["FrontendPreview:Enabled"] = enabled });
            foreach (var path in new[] { "/preview/login", "/login-preview" })
                results.Add($"{environment}/{enabled}{path}={(await host.Get(path)).Code}");
        }

        var bad = results.FirstOrDefault(r => !r.EndsWith("=404", StringComparison.Ordinal));
        Qa.Verify("QA2-F-31-preview-not-exposed", bad is null ? 404 : int.Parse(bad[^3..]), detail: string.Join(" ", results));
    }

    [Fact]
    public async Task MockAssets_AreNotServedInProduction()
    {
        var host = QaHost.Create(factory);
        var statuses = new List<string>();
        foreach (var path in new[] { "/js/auth-mock.js", "/js/auth-preview.js", "/css/auth-preview.css" })
            statuses.Add($"{path}={(await host.Get(path)).Code}");

        var bad = statuses.FirstOrDefault(s => !s.EndsWith("=404", StringComparison.Ordinal));
        Qa.Verify("QA2-F-32-mock-assets", bad is null ? 404 : int.Parse(bad[^3..]), detail: string.Join(" ", statuses));
    }
}
