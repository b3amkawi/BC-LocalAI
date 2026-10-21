using System.Reflection;
using System.Text;
using BC_LocalAI.Security;
using BC_LocalAI.Tests.Security;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>QA-002 3.5 security headers and the "no secrets in error bodies and logs" rule (Production environment).</summary>
[Collection("Qa002")]
public sealed class Qa002HeadersAndLeakTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<(QaHost Host, Acct Alice, Session Session)> Signed(Action<IServiceCollection>? services = null, bool history = true, string host = "localhost")
    {
        var qa = QaHost.Create(factory, history: history, host: host, services: services);
        var alice = qa.AddUser("alice");
        return (qa, alice, await qa.Login(alice));
    }

    private static bool Has(Resp response, string header, string contains) =>
        response.Header(header) is { } value && value.Contains(contains, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ H: headers
    [Fact]
    public async Task ChatPage_HasTheContentSecurityPolicyAndBasicHeaders()
    {
        var (host, _, _) = await Signed();

        var page = await host.Get("/");

        var csp = page.Header("Content-Security-Policy") ?? "";
        Qa.Verify("QA2-H-01-home", page.Code, ok: csp.Contains("default-src 'self'") && csp.Contains("object-src 'none'") && csp.Contains("frame-ancestors 'none'") &&
                                                  Has(page, "X-Content-Type-Options", "nosniff") && Has(page, "Referrer-Policy", "no-referrer"));
    }

    [Fact]
    public async Task AuthApi_IsNeverCached()
    {
        var (host, alice, session) = await Signed();
        var responses = new List<Resp>
        {
            await host.Get("/api/auth/csrf"),
            await host.Get("/api/auth/me", session.Pair),
            await host.LoginRaw(alice.Name, "wrong-password-" + QaHost.NewSecret()),
            await host.Logout(session)
        };

        var missing = responses.Select((r, i) => (r, i)).Where(x => !(Has(x.r, "Cache-Control", "no-store") && Has(x.r, "Pragma", "no-cache") && Has(x.r, "X-Content-Type-Options", "nosniff"))).Select(x => x.i).ToArray();
        Qa.Verify("QA2-H-02-auth-api", ok: missing.Length == 0, detail: "responses missing a header: " + string.Join(",", missing));
    }

    [Fact]
    public async Task Errors_AreNeverCached()
    {
        var (host, _, session) = await Signed();
        var unauthorized = await host.Chat(null);
        var forbidden = await host.Get("/api/__test/admin", session.Pair);
        var csrf = await host.Send(HttpMethod.Post, "/api/chat", session.Pair, new { message = "x" }, headers: [new("Sec-Fetch-Site", "cross-site")]);

        Qa.Verify("QA2-H-03-401-no-store", unauthorized.Code, unauthorized.ErrorCode, ok: Has(unauthorized, "Cache-Control", "no-store"));
        Qa.Verify("QA2-H-04-403-no-store", forbidden.Code, forbidden.ErrorCode, ok: Has(forbidden, "Cache-Control", "no-store") && csrf.Code == 403 && Has(csrf, "Cache-Control", "no-store"));
    }

    [Fact]
    public async Task ConversationResponses_AreNeverCached()
    {
        var (host, _, session) = await Signed();
        var created = await host.Browser(HttpMethod.Post, "/api/conversations", session.Pair, new { title = "t" });
        var id = created.Json.GetProperty("id").GetString();
        var all = new[] { created, await host.Get("/api/conversations", session.Pair), await host.Get($"/api/conversations/{id}", session.Pair),
                          await host.Get($"/api/conversations/{id}/messages", session.Pair), await host.Get($"/api/conversations/{Guid.NewGuid()}", session.Pair) };

        Qa.Verify("QA2-H-05-conversations-no-store", 200, ok: all.All(r => Has(r, "Cache-Control", "no-store")), detail: "statuses " + string.Join(",", all.Select(r => r.Code)));
    }

    [Fact]
    public async Task ChatAnswer_IsNotCacheable()
    {
        var (host, _, session) = await Signed();

        var chat = await host.Chat(session.Pair);

        Qa.Verify("QA2-H-06-chat-no-store", chat.Code, chat.ErrorCode, ok: Has(chat, "Cache-Control", "no-store"), detail: "Cache-Control: " + (chat.Header("Cache-Control") ?? "(none)"));
    }

    [Fact]
    public async Task Stream_HasTheSseHeaders()
    {
        var (host, _, session) = await Signed();

        var (head, _) = await host.Stream(session.Pair);

        Qa.Verify("QA2-H-07-stream-headers", head.Code, head.ErrorCode,
            ok: head.Header("Content-Type")!.StartsWith("text/event-stream", StringComparison.Ordinal) && (Has(head, "Cache-Control", "no-cache") || Has(head, "Cache-Control", "no-store")) &&
                head.Header("X-Accel-Buffering") == "no");
    }

    [Fact]
    public async Task Hsts_IsSentInProduction_ForARealHostName()
    {
        var (host, _, _) = await Signed(host: "chat.example.test");

        var page = await host.Get("/");

        Qa.Verify("QA2-H-08-hsts", page.Code, ok: page.Header("Strict-Transport-Security") is { Length: > 0 });
    }

    [Fact]
    public async Task NoFrameworkBanners_And_HtmlPagesCannotBeFramed()
    {
        var (host, _, session) = await Signed();
        var responses = new[] { await host.Get("/"), await host.Get("/api/auth/me", session.Pair), await host.Chat(session.Pair), await host.Get("/api/conversations", session.Pair) };
        Qa.Verify("QA2-H-09-no-powered-by", ok: responses.All(r => r.Header("X-Powered-By") is null && r.Header("X-AspNet-Version") is null));

        var privacy = await host.Get("/Home/Privacy");
        Qa.Verify("QA2-H-10-html-pages-frame", privacy.Code,
            ok: Has(privacy, "X-Frame-Options", "") || (privacy.Header("Content-Security-Policy") ?? "").Contains("frame-ancestors"),
            detail: "X-Frame-Options: " + (privacy.Header("X-Frame-Options") ?? "(none)") + "; CSP: " + (privacy.Header("Content-Security-Policy") ?? "(none)"));
    }

    [Fact]
    public async Task EveryApiResponse_HasNosniff()
    {
        var (host, _, session) = await Signed();
        var (stream, _) = await host.Stream(session.Pair);
        var checks = new (string Name, Resp Response)[]
        {
            ("chat", await host.Chat(session.Pair)), ("stream", stream), ("conversations", await host.Get("/api/conversations", session.Pair)),
            ("401", await host.Chat(null)), ("403", await host.Get("/api/__test/admin", session.Pair))
        };

        var missing = checks.Where(c => !Has(c.Response, "X-Content-Type-Options", "nosniff")).Select(c => c.Name).ToArray();
        Qa.Verify("QA2-H-11-api-nosniff", ok: missing.Length == 0, detail: "without nosniff: " + string.Join(",", missing));
    }

    [Fact]
    public async Task ApiErrors_AreNotFrameworkHtmlPages()
    {
        var (host, _, session) = await Signed();
        var notFound = await host.Get("/api/nope", session.Pair);
        var wrongMethod = await host.Get("/api/chat", session.Pair);
        var unsupported = await host.Send(HttpMethod.Post, "/api/chat", session.Pair, body: "x", contentType: "text/plain", headers: QaHost.BrowserHeaders);

        var all = new[] { notFound, wrongMethod, unsupported };
        Qa.Verify("QA2-H-12-error-json", ok: all.All(r => !r.Body.Contains("<html", StringComparison.OrdinalIgnoreCase) && !r.Body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)),
            detail: string.Join(",", all.Select(r => r.Code)));
    }

    // ------------------------------------------------------------------ L: leaks
    private static readonly string[] OllamaInternals = ["OLLAMA-INTERNAL-CANARY", "11434", "127.0.0.1", ".ollama", "runner", "C:\\Users", "refused"];

    private static bool Clean(string text, IEnumerable<string> forbidden) => !forbidden.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task OllamaHttp500_NeverLeaksItsMessage()
    {
        var (host, _, session) = await Signed();
        host.Ollama.Mode = "http500";
        host.Ollama.Reset();

        var chat = await host.Chat(session.Pair);
        var chatCalls = host.Ollama.Count;
        var (head, events) = await host.Stream(session.Pair);

        var logLeaks = OllamaInternals.Take(5).Where(f => host.LogText.Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
        Qa.Verify("QA2-L-01-ollama-500", chat.Code, chat.ErrorCode, chatCalls, ok: Clean(chat.Body, OllamaInternals) && logLeaks.Length == 0,
            detail: "body clean: " + Clean(chat.Body, OllamaInternals) + "; log terms found: " + string.Join("|", logLeaks) + "; lines: " +
                    string.Join(" || ", host.Logs.Entries.Where(e => OllamaInternals.Take(5).Any(f => (e.Message + e.Exception).Contains(f))).Select(e => $"{e.EventId.Id}:{e.Message}")));
        Qa.Verify("QA2-L-02-ollama-500-stream", head.Code, events.LastOrDefault()?.Code, host.Ollama.Streams, ok: events.Last().Name == "error" && Clean(head.Body, OllamaInternals) && !head.Body.Contains("completed"));
    }

    [Fact]
    public async Task OllamaDown_And_Garbage_NeverLeakDetails()
    {
        var (host, _, session) = await Signed();
        host.Ollama.Mode = "down";
        var chat = await host.Chat(session.Pair);
        var (head, events) = await host.Stream(session.Pair);
        Qa.Verify("QA2-L-03-ollama-down", chat.Code, chat.ErrorCode, ok: events.Last().Code == "AI_UNAVAILABLE" && Clean(chat.Body + head.Body, OllamaInternals), detail: "stream " + events.Last().Code);

        host.Ollama.Mode = "garbage";
        var garbage = await host.Chat(session.Pair);
        Qa.Verify("QA2-L-04-ollama-garbage", garbage.Code, garbage.ErrorCode, ok: Clean(garbage.Body, OllamaInternals) && Clean(host.LogText, ["OLLAMA-INTERNAL-CANARY", "not json"]));
    }

    /// <summary>User store that fails with a message holding a connection string, once switched on.</summary>
    private sealed class LeakyStore : IUserSecurityStateSource
    {
        public const string Canary = "Server=db.internal.example;Database=users;User ID=svc_reader;Password=LEAK-CANARY-7731"; // ci-allow-secret: fake canary string used to prove nothing leaks into logs, not a credential.
        public bool Leak { get; set; }
        public FakeUserStore? Inner { get; set; }

        public Task<UserSecurityState?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
            Leak ? throw new InvalidOperationException(Canary) : Inner!.GetAsync(userId, cancellationToken);
    }

    [Fact]
    public async Task UserStoreException_IsNotLeaked_ToTheClientOrTheLog()
    {
        var store = new LeakyStore();
        var host = QaHost.Create(factory, services: s =>
        {
            s.RemoveAll<IUserSecurityStateSource>();
            s.AddSingleton<IUserSecurityStateSource>(store);
        });
        store.Inner = host.Users;
        var session = await host.Login(host.AddUser("alice"));
        store.Leak = true;
        host.Ollama.Reset();

        var response = await host.Chat(session.Pair);

        Qa.Verify("QA2-L-05-userstore-exception", response.Code, response.ErrorCode, host.Ollama.Count,
            ok: Clean(response.Body, ["LEAK-CANARY", "Server=", "Password="]) && Clean(host.LogText, ["LEAK-CANARY", "Password=", "db.internal"]));
    }

    public class ThrowingRepository : DispatchProxy
    {
        public Func<Exception> Failure { get; set; } = () => new InvalidOperationException();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw Failure();
    }

    private async Task<(QaHost Host, Session Session)> WithFailingHistory(Func<Exception> failure)
    {
        var proxy = DispatchProxy.Create<IChatHistoryRepository, ThrowingRepository>();
        ((ThrowingRepository)(object)proxy).Failure = failure;
        var (host, _, session) = await Signed(s =>
        {
            s.RemoveAll<IChatHistoryRepository>();
            s.AddSingleton(proxy);
        });
        return (host, session);
    }

    [Fact]
    public async Task HistoryStorageDown_Is503_WithoutDetail()
    {
        var (host, session) = await WithFailingHistory(() => new ChatHistoryUnavailableException());

        var response = await host.Get("/api/conversations", session.Pair);

        Qa.Verify("QA2-L-06-history-exception", response.Code, response.ErrorCode, ok: Clean(response.Body + host.LogText, ["LEAK-CANARY", "Password=", "MySql"]));
    }

    [Fact]
    public async Task UnexpectedException_IsAGeneric500_AndItsSecretStaysOutOfTheLog()
    {
        var (host, session) = await WithFailingHistory(() => new InvalidOperationException(LeakyStore.Canary));

        var response = await host.Get("/api/conversations", session.Pair);

        Qa.Verify("QA2-L-07-unhandled-exception", response.Code, response.ErrorCode,
            ok: Clean(response.Body, ["LEAK-CANARY", "Server=", "InvalidOperation"]) && Clean(host.LogText, ["LEAK-CANARY", "Password="]),
            detail: "client body clean: " + Clean(response.Body, ["LEAK-CANARY"]) + "; log clean: " + Clean(host.LogText, ["LEAK-CANARY"]));
    }

    [Fact]
    public async Task Logs_NeverHoldCredentialsHashesTokensOrCookies()
    {
        var (host, db) = Qa002AuthenticationTests.CreateReal(factory);
        var alice = db.Add("alice");
        var bob = db.Add("bob");
        var wrong = "wrong-" + QaHost.NewSecret();
        var typed = "TYPED-QUESTION-" + QaHost.NewSecret();

        // a day of activity: failures, sign-in, chat, stream, rejections of every kind, sign-out
        await host.LoginRaw(alice.Name, wrong);
        await host.LoginRaw("zed-unknown", wrong);
        var session = await host.Login(alice);
        var other = await host.Login(bob);
        var sessionId = host.SessionIdOf(session);
        var (csrfPair, csrfToken) = await host.Csrf(session.Pair);
        await host.Chat(session.Pair, typed);
        await host.Stream(session.Pair, typed);
        await host.Send(HttpMethod.Post, "/api/chat", session.Pair, new { message = typed }, headers: [new("Origin", "https://evil.example")]);
        await host.Get("/api/__test/admin", session.Pair);
        host.Users.Unavailable = true;
        await host.Chat(session.Pair);
        host.Users.Unavailable = false;
        host.Users.Users[bob.Id] = host.Users.Users[bob.Id] with { IsActive = false };
        await host.Chat(other.Pair);
        host.Ollama.Mode = "http500";
        await host.Chat(session.Pair, typed);
        await host.Logout(session);

        var secrets = new Dictionary<string, string>
        {
            ["alice password"] = alice.Password, ["bob password"] = bob.Password, ["wrong password"] = wrong,
            ["alice hash"] = db.HashOf("alice"), ["alice stamp"] = host.Users.Users[alice.Id].SecurityStamp,
            ["session cookie"] = Uri.UnescapeDataString(session.Value), ["session cookie (raw)"] = session.Value, ["session id"] = sessionId,
            ["csrf token"] = csrfToken, ["csrf cookie"] = csrfPair[(csrfPair.IndexOf('=') + 1)..],
            ["question text"] = typed, ["configured db password"] = "placeholder-writer", ["configured reader password"] = "Password=placeholder"
        };
        var text = host.LogText;
        var leaked = secrets.Where(s => text.Contains(s.Value, StringComparison.Ordinal)).Select(s => s.Key).ToArray();

        Qa.Verify("QA2-L-08-login-failure-logs", ok: !text.Contains(wrong) && !text.Contains(alice.Password) && !text.Contains(db.HashOf("alice")), detail: "log lines: " + host.Logs.Entries.Count);
        Qa.Verify("QA2-L-09-sweep", ok: leaked.Length == 0, detail: "leaked: " + string.Join(",", leaked) + "; log lines: " + host.Logs.Entries.Count);

        var ids = host.Logs.Entries.Select(e => e.EventId.Id).Distinct().Order().ToArray();
        Qa.Verify("QA2-L-10-audit-events", ok: new[] { 5001, 5002, 5003, 5005, 5006, 5007 }.All(ids.Contains), detail: "event ids seen: " + string.Join(",", ids));
    }

    [Fact]
    public async Task MalformedRequests_NeverShowFrameworkInternals()
    {
        var (host, _, session) = await Signed();
        host.Ollama.Reset();
        var bodies = new[] { "{bad", """{"message":123}""", "[]", "", """{"message":null}""", "\u0000\u0001" };
        var chat = new List<Resp>();
        var stream = new List<Resp>();
        foreach (var body in bodies)
        {
            chat.Add(await host.Send(HttpMethod.Post, "/api/chat", session.Pair, body: body, headers: QaHost.BrowserHeaders));
            stream.Add(await host.Send(HttpMethod.Post, "/api/chat/stream", session.Pair, body: body, headers: QaHost.BrowserHeaders));
        }

        var history = await host.Send(HttpMethod.Post, "/api/conversations", session.Pair, body: "{bad", headers: QaHost.BrowserHeaders);
        string[] internals = ["System.", "Microsoft.", "LineNumber", "BytePositionInLine", " at ", ".cs", "Exception", "stack", "JsonException", "$."];
        var all = chat.Concat(stream).Append(history).ToList();
        var first = chat.Concat(stream).FirstOrDefault(r => r.Code != 400 || r.ErrorCode != "INVALID_MESSAGE");
        var leaked = all.Any(r => !Clean(r.Body, internals));

        Qa.Verify("QA2-L-11-framework-errors", first?.Code ?? 400, first is null ? "INVALID_MESSAGE" : first.ErrorCode, host.Ollama.Count, ok: !leaked,
            detail: first is null ? "" : $"first unexpected: {first.Code} {first.ErrorCode}");
    }

    [Fact]
    public async Task SessionRefusals_AllLookTheSame()
    {
        var host = QaHost.Create(factory);
        var bodies = new Dictionary<string, string>();
        foreach (var role in new[] { "anonymous", "logged-out", "disabled", "idle-expired", "role-changed" })
        {
            var cookie = await Qa002MatrixTests.CookieFor(host, role, "same");
            var response = await host.Chat(cookie);
            Assert.Equal(401, response.Code);
            bodies[role] = response.Body;
        }

        Qa.Verify("QA2-L-12-same-reason-hidden", 401, "AUTH_REQUIRED", ok: bodies.Values.Distinct().Count() == 1, detail: "distinct bodies: " + bodies.Values.Distinct().Count());
    }
}
