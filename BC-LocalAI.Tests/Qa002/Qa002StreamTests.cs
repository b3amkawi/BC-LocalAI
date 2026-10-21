using System.Diagnostics;
using System.Net;
using System.Text;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>Reads an SSE response in the background, like the chat page does, so a test can act while the stream is open.</summary>
internal sealed class SseTap
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public SseTap(HttpResponseMessage response)
    {
        Response = response;
        Completion = Task.Run(async () =>
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var chars = new char[256];
            int read;
            while ((read = await reader.ReadAsync(chars, 0, chars.Length)) > 0)
                lock (_gate) _buffer.Append(chars, 0, read);
        });
    }

    public HttpResponseMessage Response { get; }
    public Task Completion { get; }

    public string Raw { get { lock (_gate) return _buffer.ToString(); } }

    public List<SseEvent> Events => QaHost.Parse(Raw);

    public async Task WaitFor(string text)
    {
        var timer = Stopwatch.StartNew();
        while (!Raw.Contains(text, StringComparison.Ordinal))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new Xunit.Sdk.XunitException($"The stream never delivered '{text}'. Got: {Raw}");
            await Task.Delay(10);
        }
    }

    public async Task<bool> EndsWithin(TimeSpan span) => await Task.WhenAny(Completion, Task.Delay(span)) == Completion;
}

/// <summary>QA-002 3.6 / 3.7: unauthenticated calls never reach Ollama; a session that ends during a stream ends the stream safely; one user flooding.</summary>
[Collection("Qa002")]
public sealed class Qa002StreamTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Before = "BEFORE-REVOKE-CANARY-";
    private const string After = "AFTER-REVOKE-CANARY-";

    private async Task<(QaHost Host, Acct Alice, Session Session)> Signed(bool history = false, IDictionary<string, string?>? settings = null)
    {
        var host = QaHost.Create(factory, history: history, settings: settings);
        var alice = host.AddUser("alice");
        return (host, alice, await host.Login(alice));
    }

    // ------------------------------------------------------------------ U
    [Fact]
    public async Task Anonymous_Chat_And_Stream_NeverReachOllama()
    {
        var (host, _, _) = await Signed();
        var chat = new List<Resp>();
        var stream = new List<Resp>();
        for (var i = 0; i < 10; i++)
        {
            chat.Add(await host.Chat(null));
            stream.Add(await host.Chat(null, stream: true));
        }

        Qa.Verify("QA2-U-01-anon-chat-x10", chat.All(r => r.Code == 401) ? 401 : chat.First(r => r.Code != 401).Code,
            chat.All(r => r.ErrorCode == "AUTH_REQUIRED") ? "AUTH_REQUIRED" : "other", host.Ollama.Count);
        Qa.Verify("QA2-U-02-anon-stream-x10", stream.All(r => r.Code == 401) ? 401 : stream.First(r => r.Code != 401).Code,
            stream.All(r => r.ErrorCode == "AUTH_REQUIRED") ? "AUTH_REQUIRED" : "other", host.Ollama.Count,
            ok: stream.All(r => r.Header("Content-Type")!.StartsWith("application/json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Anonymous_BadBodies_AreAnswered401_BeforeValidation()
    {
        var (host, _, _) = await Signed();
        var results = new List<Resp>();
        foreach (var body in new[] { "", "{bad", """{"message":""}""", """{"message":123}""" })
        {
            results.Add(await host.Send(HttpMethod.Post, "/api/chat", body: body, headers: QaHost.BrowserHeaders));
            results.Add(await host.Send(HttpMethod.Post, "/api/chat/stream", body: body, headers: QaHost.BrowserHeaders));
        }

        var first = results.FirstOrDefault(r => r.Code != 401);
        Qa.Verify("QA2-U-03-anon-invalid-body", first?.Code ?? 401, first?.ErrorCode ?? "AUTH_REQUIRED", host.Ollama.Count);
    }

    [Fact]
    public async Task Anonymous_HugeBody_IsRefused_BeforeOllama()
    {
        var (host, _, _) = await Signed();
        var huge = "{\"message\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";

        var chat = await host.Send(HttpMethod.Post, "/api/chat", body: huge, headers: QaHost.BrowserHeaders);
        var stream = await host.Send(HttpMethod.Post, "/api/chat/stream", body: huge, headers: QaHost.BrowserHeaders);

        Qa.Verify("QA2-U-04-anon-oversized", ollama: host.Ollama.Count, ok: chat.Code is 401 or 413 && stream.Code is 401 or 413, detail: $"chat {chat.Code}, stream {stream.Code}");
    }

    [Fact]
    public async Task Anonymous_WithConversationId_Is401()
    {
        var (host, _, _) = await Signed(history: true);

        var chat = await host.Chat(null, "hello", Guid.NewGuid().ToString());
        var stream = await host.Chat(null, "hello", Guid.NewGuid().ToString(), stream: true);

        Qa.Verify("QA2-U-05-anon-conversation-id", chat.Code, chat.ErrorCode, host.Ollama.Count, ok: stream.Code == 401);
    }

    [Fact]
    public async Task EveryKindOfRefusal_LeavesOllamaUntouched()
    {
        var (host, alice, session) = await Signed();
        var other = await host.Login(host.AddUser("bob"));
        var codes = new List<int>
        {
            (await host.Send(HttpMethod.Post, "/api/chat", session.Pair, new { message = "x" }, headers: [new("Sec-Fetch-Site", "cross-site")])).Code,
            (await host.Send(HttpMethod.Post, "/api/chat/stream", session.Pair, new { message = "x" }, headers: [new("Origin", "https://evil.example")])).Code,
            (await host.Send(HttpMethod.Post, "/api/chat", session.Pair, body: "message=x", contentType: "application/x-www-form-urlencoded")).Code
        };
        await host.Logout(session);
        codes.Add((await host.Chat(session.Pair)).Code);
        host.Users.Users[host.Accounts["BOB"].Id] = host.Users.Users[host.Accounts["BOB"].Id] with { IsActive = false };
        codes.Add((await host.Chat(other.Pair, stream: true)).Code);
        host.Clock.Advance(TimeSpan.FromMinutes(31));
        codes.Add((await host.Chat(session.Pair)).Code);

        Qa.Verify("QA2-U-06-rejected-never-reach", ollama: host.Ollama.Count, ok: codes.SequenceEqual([403, 403, 415, 401, 401, 401]), detail: string.Join(",", codes) + " " + alice.Name);
    }

    [Fact]
    public async Task InvalidMessage_IsRefused_BeforeOllama()
    {
        var (host, _, session) = await Signed();

        var chat = await host.Send(HttpMethod.Post, "/api/chat", session.Pair, new { message = "  " }, headers: QaHost.BrowserHeaders);
        var stream = await host.Send(HttpMethod.Post, "/api/chat/stream", session.Pair, new { message = "" }, headers: QaHost.BrowserHeaders);

        Qa.Verify("QA2-U-07-invalid-message", chat.Code, chat.ErrorCode, host.Ollama.Count, ok: stream.Code == 400 && stream.ErrorCode == "INVALID_MESSAGE");
    }

    [Fact]
    public async Task Counter_CountsOneRequestPerCall()
    {
        var (host, _, session) = await Signed();

        var chat = await host.Chat(session.Pair);
        var afterChat = host.Ollama.Count;
        var (head, _) = await host.Stream(session.Pair);

        Qa.Verify("QA2-U-08-counter-sanity", chat.Code, chat.ErrorCode, afterChat, ok: head.Code == 200 && host.Ollama.Count == 2 && host.Ollama.Streams == 1);
    }

    [Fact]
    public async Task PlainHttp_NeverReachesOllama()
    {
        // The HTTPS redirect needs the deployment to know its HTTPS port (ASPNETCORE_HTTPS_PORT / https_port). Without it the
        // middleware does nothing and says so in a warning: measured first, as a note for the report.
        var (bare, _, bareSession) = await Signed();
        var withoutPort = await bare.Send(HttpMethod.Post, "/api/chat", bareSession.Pair, new { message = "x" }, http: true);

        var (host, _, session) = await Signed(settings: new Dictionary<string, string?> { ["https_port"] = "443" });
        var chat = await host.Send(HttpMethod.Post, "/api/chat", session.Pair, new { message = "x" }, http: true);
        var stream = await host.Send(HttpMethod.Post, "/api/chat/stream", session.Pair, new { message = "x" }, http: true);

        Qa.Verify("QA2-U-09-http-scheme", ollama: host.Ollama.Count, ok: chat.Code != 200 && stream.Code != 200,
            detail: $"with the HTTPS port configured: chat {chat.Code}, stream {stream.Code}; without it: chat {withoutPort.Code} (Ollama calls {bare.Ollama.Count})");
    }

    // ------------------------------------------------------------------ MS
    private static ScriptedStream Script(bool toGate = true) =>
        new ScriptedStream().Open().Text(Before + "ONE ABCDEFGHIJKLMNOPQRSTUVWXYZ ").Text(Before + "TWO ABCDEFGHIJKLMNOPQRSTUVWXYZ ")
            .Gate("revoke").Text(After + "ONE ABCDEFGHIJKLMNOPQRSTUVWXYZ ").Text(After + "TWO ABCDEFGHIJKLMNOPQRSTUVWXYZ ").Finish();

    private static async Task<(SseTap Tap, ScriptedStream Script)> StartPaced(QaHost host, string cookie, string? conversationId = null)
    {
        var script = Script();
        host.Ollama.Custom = (stream, _) => Task.FromResult(script.ToResponse());
        var tap = new SseTap(await host.OpenStream(cookie, "hello", conversationId));
        await script.Reached("revoke");
        await tap.WaitFor(Before + "ONE"); // the user has already received the first part
        return (tap, script);
    }

    private static void AssertSafeEnd(string id, QaHost host, SseTap tap, ScriptedStream script, string? sessionEndedCode = "AUTH_SESSION_ENDED")
    {
        var events = tap.Events;
        var names = events.Select(e => e.Name).ToList();
        var deltas = string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Json.GetProperty("text").GetString()));
        var terminal = events.Where(e => e.Name is "completed" or "error").ToList();
        var error = events.LastOrDefault(e => e.Name == "error");
        Qa.Verify(id, (int)tap.Response.StatusCode, error?.Code, host.Ollama.Count,
            ok: names.First() == "started" && terminal.Count == 1 && error is not null && error.Code == sessionEndedCode && !names.Contains("completed") &&
                deltas.Contains(Before) && !tap.Raw.Contains(After, StringComparison.Ordinal) && tap.Raw.TrimEnd().EndsWith('}') && script.Closed.IsCompleted,
            detail: "events: " + string.Join(",", names));
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("revoke-all")]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("stamp")]
    [InlineData("absolute")]
    public async Task SessionEndedDuringStream_EndsItSafely(string how)
    {
        var (host, alice, session) = await Signed();
        var (tap, script) = await StartPaced(host, session.Pair);
        switch (how)
        {
            case "logout": Assert.Equal(204, (await host.Logout(session)).Code); break;
            case "revoke-all": Assert.Equal(200, (await host.Send(HttpMethod.Post, $"/api/__test/revoke-all/{alice.Id}")).Code); break;
            case "disabled": host.Users.Users[alice.Id] = host.Users.Users[alice.Id] with { IsActive = false }; host.Clock.Advance(StreamSessionGuard.AccountCheckInterval); break;
            case "role": host.Users.Users[alice.Id] = host.Users.Users[alice.Id] with { Role = BcRoles.Admin }; host.Clock.Advance(StreamSessionGuard.AccountCheckInterval); break;
            case "stamp": host.Users.Users[alice.Id] = host.Users.Users[alice.Id] with { SecurityStamp = "ROTATED" }; host.Clock.Advance(StreamSessionGuard.AccountCheckInterval); break;
            default: host.Clock.Advance(TimeSpan.FromHours(8)); break;
        }

        script.Release("revoke");
        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        await script.Closed.WaitAsync(TimeSpan.FromSeconds(5));

        AssertSafeEnd($"QA2-MS-{how}", host, tap, script);
        if (how == "logout")
        {
            var reuse = await host.Chat(session.Pair);
            Qa.Verify("QA2-MS-reuse", reuse.Code, reuse.ErrorCode, host.Ollama.Count);
            var last = tap.Raw.TrimEnd().Split("\n\n").Last();
            var data = System.Text.Json.JsonDocument.Parse(last.Split('\n')[1]["data: ".Length..]).RootElement;
            Qa.Verify("QA2-MS-wire-format", (int)tap.Response.StatusCode, data.GetProperty("code").GetString(), host.Ollama.Count,
                ok: last.StartsWith("event: error\n", StringComparison.Ordinal) && data.TryGetProperty("message", out _) && data.GetProperty("partial").GetBoolean());
        }
    }

    [Fact]
    public async Task UserStoreDownDuringStream_EndsItFailClosed_WithoutRevoking()
    {
        var (host, _, session) = await Signed();
        var (tap, script) = await StartPaced(host, session.Pair);
        host.Users.Unavailable = true;
        host.Clock.Advance(StreamSessionGuard.AccountCheckInterval);

        script.Release("revoke");
        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        host.Users.Unavailable = false;
        var me = await host.Get("/api/auth/me", session.Pair);

        AssertSafeEnd("QA2-MS-store-down", host, tap, script);
        Assert.Equal(200, me.Code); // the database is back and the session was not revoked
    }

    [Fact]
    public async Task LongActiveStream_IsNotCutByTheIdleTimeout()
    {
        var (host, _, session) = await Signed();
        var script = new ScriptedStream().Open();
        for (var i = 1; i <= 4; i++) script.Text($"PIECE-{i}-ABCDEFGHIJKLMNOPQRSTUVWXYZ ").Gate("g" + i);
        script.Finish();
        host.Ollama.Custom = (_, _) => Task.FromResult(script.ToResponse());
        var tap = new SseTap(await host.OpenStream(session.Pair));

        for (var i = 1; i <= 4; i++)
        {
            await script.Reached("g" + i);
            host.Clock.Advance(TimeSpan.FromMinutes(10)); // 40 minutes in all, a piece every 10
            script.Release("g" + i);
        }

        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Qa.Verify("QA2-MS-long-active", (int)tap.Response.StatusCode, null, host.Ollama.Count, ok: tap.Events.Last().Name == "completed" && !tap.Raw.Contains("event: error"));
    }

    [Fact]
    public async Task RevokedWhileTheModelIsSilent_EndsWithinTenSeconds()
    {
        var (host, _, session) = await Signed();
        var (tap, script) = await StartPaced(host, session.Pair);
        Assert.Equal(204, (await host.Logout(session)).Code);

        // the model is thinking and sends nothing: the gate stays closed
        var ended = await tap.EndsWithin(TimeSpan.FromSeconds(10));
        var error = tap.Events.LastOrDefault(e => e.Name == "error");
        script.Release("revoke"); // let the fake finish so the test can clean up
        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Qa.Verify("QA2-MS-stalled-model", (int)tap.Response.StatusCode, error?.Code, host.Ollama.Count, ok: ended,
            detail: ended ? "ended while silent" : "the stream stayed open for 10 s after sign-out while Ollama sent nothing; it ended only when Ollama produced its next piece");
    }

    [Fact]
    public async Task SessionEndedDuringStream_InAConversation_IsStoredAsFailed()
    {
        var (host, alice, session) = await Signed(history: true);
        var conversation = (await host.Browser(HttpMethod.Post, "/api/conversations", session.Pair, new { title = "t" })).Json.GetProperty("id").GetString()!;
        var (tap, script) = await StartPaced(host, session.Pair, conversation);
        Assert.Equal(204, (await host.Logout(session)).Code);
        script.Release("revoke");
        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        var again = await host.Login(alice);
        var messages = (await host.Get($"/api/conversations/{conversation}/messages", again.Pair)).Json.GetProperty("items").EnumerateArray().ToList();
        var answer = messages.Last();

        Qa.Verify("QA2-MS-history", (int)tap.Response.StatusCode, tap.Events.LastOrDefault(e => e.Name == "error")?.Code, host.Ollama.Count,
            ok: messages.Count == 2 && answer.GetProperty("status").GetString() == "failed" && answer.GetProperty("errorCode").GetString() == "AUTH_SESSION_ENDED" &&
                answer.GetProperty("content").GetString()!.Contains(Before) && !answer.GetProperty("content").GetString()!.Contains(After),
            detail: "answer status " + answer.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SessionEndedDuringChat_ReturnsNoAnswer()
    {
        var (host, _, session) = await Signed();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ollama.Custom = async (_, _) =>
        {
            reached.TrySetResult();
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"qwen3:4b","response":"{\"answer\":\"SECRET-ANSWER-AFTER-LOGOUT\"}","done":true,"done_reason":"stop"}""", Encoding.UTF8, "application/json")
            };
        };

        var pending = host.Chat(session.Pair);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(204, (await host.Logout(session)).Code);
        gate.SetResult();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(20));

        Qa.Verify("QA2-MS-chat", response.Code, response.ErrorCode, host.Ollama.Count, ok: !response.Body.Contains("SECRET-ANSWER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SomeoneElsesRevocation_DoesNotEndMyStream()
    {
        var (host, _, session) = await Signed();
        var bob = host.AddUser("bob");
        await host.Login(bob);
        var (tap, script) = await StartPaced(host, session.Pair);
        Assert.Equal(200, (await host.Send(HttpMethod.Post, $"/api/__test/revoke-all/{bob.Id}")).Code);
        script.Release("revoke");
        await tap.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Qa.Verify("QA2-MS-other-user", (int)tap.Response.StatusCode, null, host.Ollama.Count, ok: tap.Events.Last().Name == "completed" && tap.Raw.Contains(After));
    }

    // ------------------------------------------------------------------ Q
    private sealed class OnDispose(Stream inner, Action action) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { action(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Ollama with OLLAMA_NUM_PARALLEL=1: one generation at a time, the others wait for it.</summary>
    private static void SerialOllama(QaHost host, TimeSpan work, Action<int>? inFlight = null)
    {
        var slot = new SemaphoreSlim(1);
        var active = 0;
        host.Ollama.Custom = async (_, token) =>
        {
            await slot.WaitAsync(token);
            inFlight?.Invoke(Interlocked.Increment(ref active));
            try { await Task.Delay(work, token); }
            catch { Interlocked.Decrement(ref active); slot.Release(); throw; }
            var body = FakeOllama.Record("{\"answer\":\"A long enough first piece of a queued answer. ") + FakeOllama.Record("Second piece of the queued answer.\"}") + FakeOllama.Final();
            var content = new StreamContent(new OnDispose(new MemoryStream(Encoding.UTF8.GetBytes(body)), () => { Interlocked.Decrement(ref active); slot.Release(); }));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
    }

    private static readonly string[] DocumentedCodes = ["AI_TIMEOUT", "AI_UNAVAILABLE", "AI_PROVIDER_ERROR", "AI_INVALID_RESPONSE", "AI_RESPONSE_TOO_LARGE"];

    [Fact]
    public async Task OneUserFlooding_OtherUsersStillGetAnAnswerOrAClearError()
    {
        // QA2-D06 baseline: the case records what happens WITHOUT a per-user cap, so BE-008's cap is lifted here.
        // The BE-008 behavior (429 for the extra requests) is tested in ChatAdmission/ChatAdmissionApiTests.
        var (host, _, alice) = await Signed(settings: new Dictionary<string, string?> { ["ChatQueue:MaxPendingPerUser"] = "16", ["ChatQueue:MaxQueueLength"] = "32" });
        var bob = await host.Login(host.AddUser("bob"));
        SerialOllama(host, TimeSpan.FromMilliseconds(300));
        var timer = Stopwatch.StartNew();

        var floods = Enumerable.Range(0, 6).Select(_ => Task.Run(() => host.Stream(alice.Pair, "flood"))).ToList();
        await Task.Delay(150);
        var other = Task.Run(async () => (Result: await host.Stream(bob.Pair, "bob question"), Elapsed: timer.Elapsed));
        var all = await Task.WhenAll(floods.Select(async f => (await f).Events).Append(Task.Run(async () => (await other).Result.Events))).WaitAsync(TimeSpan.FromSeconds(60));
        var bobResult = await other;

        bool Understood(List<SseEvent> events) =>
            events.Count > 0 && (events.Last().Name == "completed" || (events.Last().Name == "error" && DocumentedCodes.Contains(events.Last().Code)));
        Qa.Verify("QA2-Q-01-one-user-many", bobResult.Result.Head.Code, null, host.Ollama.Count,
            ok: all.All(Understood) && bobResult.Elapsed < TimeSpan.FromSeconds(30),
            detail: $"bob finished after {bobResult.Elapsed.TotalSeconds:F1}s as {bobResult.Result.Events.Last().Name}; all {all.Length} ended understood: {all.All(Understood)}");
        var refused = floods.Count(f => f.Result.Head.Code == 429);
        Qa.Observe("QA2-Q-03-per-user-cap", refused > 0 ? 429 : 200, null, null,
            refused > 0 ? $"{refused} of 6 concurrent requests of one user were refused" : "all 6 concurrent requests of one user reached Ollama; no per-user limit (the queue is Ollama's)");
    }

    [Fact]
    public async Task LongQueue_EndsInAClearTimeoutMessage()
    {
        var (host, _, alice) = await Signed(settings: new Dictionary<string, string?> { ["Ollama:TimeoutSeconds"] = "2" });
        var bob = await host.Login(host.AddUser("bob"));
        SerialOllama(host, TimeSpan.FromSeconds(4)); // the first request holds Ollama longer than the deadline

        var first = Task.Run(() => host.Stream(alice.Pair, "slow"));
        await Task.Delay(300);
        var (head, events) = await host.Stream(bob.Pair, "waiting");
        await first;

        var error = events.LastOrDefault(e => e.Name == "error");
        Qa.Verify("QA2-Q-02-timeout-message", (int)head.Code, error?.Code, ok: error is not null && error.Json.GetProperty("message").GetString() is { Length: > 3 } && !error.Json.GetProperty("message").GetString()!.Contains("Exception"),
            detail: "events " + string.Join(",", events.Select(e => e.Name)));
    }
}
