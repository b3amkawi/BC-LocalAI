using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>QA-002 3.5 CSRF and cross-site requests against every state-changing endpoint, including the streaming fetch POST.</summary>
[Collection("Qa002")]
public sealed class Qa002CsrfTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] Endpoints = ["chat", "stream", "conv-create", "conv-patch", "conv-delete", "logout"];

    /// <summary>Header lists of the cross-site variants, in the order of the catalog.</summary>
    private static readonly (string Id, (string Name, string Value)[] Headers)[] Variants =
    [
        ("sfs-cross-site", [("Sec-Fetch-Site", "cross-site")]),
        ("sfs-same-site", [("Sec-Fetch-Site", "same-site")]),
        ("origin-evil", [("Origin", "https://evil.example")]),
        ("origin-null", [("Origin", "null")]),
        ("origin-http-same-host", [("Origin", "http://localhost")]),
        ("origin-double", [("Origin", "https://localhost"), ("Origin", "https://evil.example")])
    ];

    private sealed record World(QaHost Host, Session Alice, string ConversationId);

    private async Task<World> Create()
    {
        var host = QaHost.Create(factory, history: true);
        var alice = await host.Login(host.AddUser("alice"));
        var created = await host.Browser(HttpMethod.Post, "/api/conversations", alice.Pair, new { title = "original" });
        return new World(host, alice, created.Json.GetProperty("id").GetString()!);
    }

    private static async Task<Resp> Send(World w, string endpoint, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var host = w.Host;
        var cookie = w.Alice.Pair;
        switch (endpoint)
        {
            case "chat": return await host.Send(HttpMethod.Post, "/api/chat", cookie, new { message = "hello" }, headers: headers);
            case "stream": return await host.Send(HttpMethod.Post, "/api/chat/stream", cookie, new { message = "hello" }, headers: headers);
            case "conv-create": return await host.Send(HttpMethod.Post, "/api/conversations", cookie, new { title = "forged" }, headers: headers);
            case "conv-patch": return await host.Send(HttpMethod.Patch, $"/api/conversations/{w.ConversationId}", cookie, new { title = "forged" }, headers: headers);
            case "conv-delete": return await host.Send(HttpMethod.Delete, $"/api/conversations/{w.ConversationId}", cookie, headers: headers);
            default:
                var (pair, token) = await host.Csrf(cookie); // a valid token: the origin check alone must refuse
                return await host.Send(HttpMethod.Post, "/api/auth/logout", cookie + "; " + pair, headers: headers.Append(new("X-CSRF-TOKEN", token)));
        }
    }

    /// <summary>The state a forged request must not have touched.</summary>
    private static async Task<bool> Untouched(World w)
    {
        var conversation = await w.Host.Get($"/api/conversations/{w.ConversationId}", w.Alice.Pair);
        var list = await w.Host.Get("/api/conversations", w.Alice.Pair);
        var me = await w.Host.Get("/api/auth/me", w.Alice.Pair);
        return conversation.Code == 200 && conversation.Json.GetProperty("title").GetString() == "original" &&
               list.Json.GetProperty("items").GetArrayLength() == 1 && me.Code == 200;
    }

    public static IEnumerable<object[]> Cases() =>
        from endpoint in Endpoints from variant in Variants select new object[] { endpoint, variant.Id };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CrossSiteRequest_IsRefused_WithoutSideEffects(string endpoint, string variant)
    {
        var w = await Create();
        var headers = Variants.Single(v => v.Id == variant).Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
        w.Host.Ollama.Reset();

        var response = await Send(w, endpoint, headers);

        Qa.Verify($"QA2-C-{endpoint}-{variant}", response.Code, response.ErrorCode, w.Host.Ollama.Count, ok: await Untouched(w));
    }

    [Fact]
    public async Task SameOriginBrowserRequests_Work_ForEveryEndpoint()
    {
        var w = await Create();
        var calls = new (string Endpoint, int Success)[] { ("chat", 200), ("stream", 200), ("conv-create", 201), ("conv-patch", 200), ("conv-delete", 204) };
        var results = new List<int>();
        foreach (var (endpoint, success) in calls)
        {
            var code = (await Send(w, endpoint, QaHost.BrowserHeaders)).Code;
            results.Add(code == success ? 200 : code); // every endpoint answers with its own success status
        }

        Qa.Verify("QA2-C-allow-same-origin", results.All(r => r == 200) ? 200 : results.First(r => r != 200), ok: true, detail: string.Join(",", results));
    }

    [Fact]
    public async Task SecFetchSiteNone_And_ScriptsWithoutBrowserHeaders_AreNotBlocked()
    {
        var w = await Create();

        var none = await Send(w, "chat", [new("Sec-Fetch-Site", "none")]);
        Qa.Verify("QA2-C-allow-sfs-none", none.Code, none.ErrorCode);

        var script = await Send(w, "chat", []);
        Qa.Verify("QA2-C-allow-no-browser-headers", script.Code, script.ErrorCode);
    }

    [Fact]
    public async Task Forms_AndPlainText_AreRefusedBeforeAnythingRuns()
    {
        var w = await Create();
        w.Host.Ollama.Reset();
        var plain = new[]
        {
            await w.Host.Send(HttpMethod.Post, "/api/chat", w.Alice.Pair, body: """{"message":"hello"}""", contentType: "text/plain"),
            await w.Host.Send(HttpMethod.Post, "/api/chat/stream", w.Alice.Pair, body: """{"message":"hello"}""", contentType: "text/plain"),
            await w.Host.Send(HttpMethod.Post, "/api/conversations", w.Alice.Pair, body: """{"title":"forged"}""", contentType: "text/plain")
        };
        var form = new[]
        {
            await w.Host.Send(HttpMethod.Post, "/api/chat", w.Alice.Pair, body: "message=hello", contentType: "application/x-www-form-urlencoded"),
            await w.Host.Send(HttpMethod.Post, "/api/chat/stream", w.Alice.Pair, body: "message=hello", contentType: "application/x-www-form-urlencoded"),
            await w.Host.Send(HttpMethod.Post, "/api/conversations", w.Alice.Pair, body: "title=forged", contentType: "application/x-www-form-urlencoded")
        };

        var plainCode = plain.All(r => r.Code == 415) ? 415 : plain.First(r => r.Code != 415).Code;
        var formCode = form.All(r => r.Code == 415) ? 415 : form.First(r => r.Code != 415).Code;
        Qa.Verify("QA2-C-simple-text-plain", plainCode, null, w.Host.Ollama.Count, ok: await Untouched(w));
        Qa.Verify("QA2-C-simple-form-urlencoded", formCode, null, w.Host.Ollama.Count, ok: await Untouched(w));
    }

    [Fact]
    public async Task Cors_IsNotEnabled()
    {
        var w = await Create();
        var headers = new KeyValuePair<string, string>[] { new("Origin", "https://evil.example"), new("Access-Control-Request-Method", "POST"), new("Access-Control-Request-Headers", "content-type") };
        var preflight = await w.Host.Send(HttpMethod.Options, "/api/chat", headers: headers);
        var read = await w.Host.Get("/api/conversations", w.Alice.Pair, [new("Origin", "https://evil.example")]);
        var chat = await w.Host.Send(HttpMethod.Post, "/api/chat", w.Alice.Pair, new { message = "hello" }, headers: [new("Origin", "https://evil.example")]);

        var all = new[] { preflight, read, chat };
        Qa.Verify("QA2-C-cors-none", ok: all.All(r => r.Header("Access-Control-Allow-Origin") is null && r.Header("Access-Control-Allow-Credentials") is null),
            detail: $"preflight {preflight.Code}, cross-origin read {read.Code}, cross-origin chat {chat.Code}");
    }

    private async Task StreamFetch(string id, bool withToken)
    {
        var w = await Create();
        w.Host.Ollama.Reset();
        using var client = w.Host.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent("""{"message":"hello"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        foreach (var (key, value) in QaHost.BrowserHeaders) request.Headers.TryAddWithoutValidation(key, value);
        var cookie = w.Alice.Pair;
        if (withToken)
        {
            var (pair, token) = await w.Host.Csrf(w.Alice.Pair);
            cookie += "; " + pair;
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        // read the stream the way the page does: chunk by chunk until the end
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var text = new StringBuilder();
        var buffer = new char[256];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(20))) > 0) text.Append(buffer, 0, read);

        var events = QaHost.Parse(text.ToString());
        var names = events.Select(e => e.Name).ToList();
        Qa.Verify(id, (int)response.StatusCode, null, w.Host.Ollama.Count,
            ok: response.Content.Headers.ContentType?.MediaType == "text/event-stream" && names.First() == "started" && names.Last() == "completed" &&
                names.Count(n => n == "delta") >= 1 && names.Count(n => n is "completed" or "error") == 1, detail: string.Join(",", names));
    }

    [Fact] public Task StreamFetch_WithoutToken_ReadsTheStream() => StreamFetch("QA2-C-stream-fetch-no-token", withToken: false);

    [Fact] public Task StreamFetch_WithToken_ReadsTheStream() => StreamFetch("QA2-C-stream-fetch-with-token", withToken: true);

    [Fact]
    public async Task MethodOverrideHeader_IsIgnored_AndGetNeverChangesState()
    {
        var w = await Create();

        var overridden = await w.Host.Get($"/api/conversations/{w.ConversationId}", w.Alice.Pair, [new("X-HTTP-Method-Override", "DELETE"), new("X-Method-Override", "DELETE")]);
        var still = await w.Host.Get($"/api/conversations/{w.ConversationId}", w.Alice.Pair);
        Qa.Verify("QA2-C-method-override", overridden.Code, overridden.ErrorCode, ok: still.Code == 200);

        var before = (await w.Host.Get("/api/conversations", w.Alice.Pair)).Body;
        foreach (var path in new[] { "/", "/api/auth/me", "/api/auth/csrf", "/api/conversations", $"/api/conversations/{w.ConversationId}", $"/api/conversations/{w.ConversationId}/messages", "/health" })
            await w.Host.Get(path, w.Alice.Pair, [new("Sec-Fetch-Site", "cross-site")]);
        var after = (await w.Host.Get("/api/conversations", w.Alice.Pair)).Body;
        Qa.Verify("QA2-C-get-safe", 200, ok: before == after && (await w.Host.Get("/api/auth/me", w.Alice.Pair)).Code == 200);
    }
}
