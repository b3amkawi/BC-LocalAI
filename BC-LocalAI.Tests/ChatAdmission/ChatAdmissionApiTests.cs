using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Security;
using BC_LocalAI.Services;
using BC_LocalAI.Services.ChatAdmission;
using BC_LocalAI.Tests.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.ChatAdmission;

/// <summary>
/// AI double whose calls stay open until the test lets them finish, so the tests decide exactly who is "in Ollama".
/// Mock evidence: it proves the gate logic through the real HTTP pipeline; it says nothing about a real Ollama.
/// </summary>
internal sealed class GatedAi : IAIService, IStreamingAIService
{
    private readonly SemaphoreSlim _release = new(0);
    private int _calls;
    private int _cancelledCalls;
    private int _running;
    private int _maxRunning;

    public int Calls => Volatile.Read(ref _calls);
    public int CancelledCalls => Volatile.Read(ref _cancelledCalls);
    public int MaxRunning => Volatile.Read(ref _maxRunning);
    public bool FailNext { get; set; }

    /// <summary>Lets <paramref name="count"/> open calls finish.</summary>
    public void Finish(int count = 1) => _release.Release(count);

    private async Task Run(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
        try
        {
            if (FailNext)
            {
                FailNext = false;
                throw AiServiceException.ProviderError();
            }

            await _release.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelledCalls);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public async Task<ChatResponse> GenerateResponseAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Run(cancellationToken);
        return new ChatResponse("answer", "qwen3:4b");
    }

    public async Task<AiStreamCompletion> StreamResponseAsync(
        string message, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Run(cancellationToken);
        await onDelta("answer", cancellationToken);
        return new AiStreamCompletion("stop", 1);
    }
}

public sealed class ChatAdmissionApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record App(HttpClient Client, GatedAi Ai, ChatAdmissionQueue Gate);

    private App Start(int concurrent = 1, int queue = 8, int perUser = 16, int wait = 30, bool queueEvents = false, string retryAfter = "10")
    {
        var ai = new GatedAi();
        var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.PinOllama();
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatQueue:MaxConcurrentRequests"] = concurrent.ToString(),
                ["ChatQueue:MaxQueueLength"] = queue.ToString(),
                ["ChatQueue:MaxPendingPerUser"] = perUser.ToString(),
                ["ChatQueue:MaxQueueWaitSeconds"] = wait.ToString(),
                ["ChatQueue:RetryAfterSeconds"] = retryAfter,
                ["ChatQueue:EmitQueueEvents"] = queueEvents ? "true" : "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAIService>(ai);
                services.AddSingleton<IStreamingAIService>(ai);
            });
        });
        return new App(app.CreateClient(), ai, app.Services.GetRequiredService<ChatAdmissionQueue>());
    }

    private static StringContent Body(string message = "hello") =>
        new(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Chat(HttpClient client, CancellationToken token = default) =>
        client.PostAsync("/api/chat", Body(), token);

    private static Task<HttpResponseMessage> Stream(HttpClient client, CancellationToken token = default) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream") { Content = Body() }, HttpCompletionOption.ResponseHeadersRead, token);

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static async Task<JsonElement> Error(HttpResponseMessage response)
    {
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType + "");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").Clone();
    }

    private static List<string> EventNames(string sse) => sse
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(block => block.Split('\n')[0].Replace("event: ", ""))
        .ToList();

    [Fact]
    public async Task Chat_UserOverQuota_Gets429WithRetryAfter_AndTheFirstRequestIsUnharmed()
    {
        var app = Start(perUser: 1);
        var first = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");

        var refused = await Chat(app.Client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("10", Assert.Single(refused.Headers.GetValues("Retry-After")));
        var error = await Error(refused);
        Assert.Equal("AI_REQUEST_IN_PROGRESS", error.GetProperty("code").GetString());
        Assert.Equal(1, app.Ai.Calls);

        app.Ai.Finish();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        // Recovery: the quota came back, the next question is served.
        app.Ai.Finish();
        Assert.Equal(HttpStatusCode.OK, (await Chat(app.Client)).StatusCode);
        Assert.Equal((0, 0), app.Gate.Snapshot());
    }

    [Fact]
    public async Task Chat_FullQueue_Gets503BusyBeforeAnythingIsSent()
    {
        var app = Start(concurrent: 1, queue: 1);
        var running = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");
        var waiting = Chat(app.Client);
        await Until(() => app.Gate.Snapshot().Queued == 1, "second request waits");

        var refused = await Chat(app.Client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("10", Assert.Single(refused.Headers.GetValues("Retry-After")));
        Assert.Equal("AI_BUSY", (await Error(refused)).GetProperty("code").GetString());
        Assert.Equal(1, app.Ai.Calls); // neither the refused nor the waiting request reached the model

        app.Ai.Finish(2);
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await waiting).StatusCode);
        Assert.Equal(2, app.Ai.Calls);
        Assert.Equal(1, app.Ai.MaxRunning);
        Assert.Equal((0, 0), app.Gate.Snapshot());
    }

    [Fact]
    public async Task Stream_FullQueue_IsAPlainHttp503_NotAnSseStream()
    {
        var app = Start(concurrent: 1, queue: 0);
        var running = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");

        var refused = await Stream(app.Client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("application/json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Equal("10", Assert.Single(refused.Headers.GetValues("Retry-After")));
        var text = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("event:", text);
        Assert.Contains("AI_BUSY", text);

        app.Ai.Finish();
        await running;
    }

    [Fact]
    public async Task Stream_UserOverQuota_IsAPlainHttp429()
    {
        var app = Start(perUser: 1);
        var first = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");

        var refused = await Stream(app.Client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("AI_REQUEST_IN_PROGRESS", (await Error(refused)).GetProperty("code").GetString());
        app.Ai.Finish();
        await first;
    }

    [Fact]
    public async Task BothEndpoints_ShareOneCapacity()
    {
        var app = Start(concurrent: 1, queue: 1);
        var plain = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "non-streaming request reaches the model");
        var streaming = Stream(app.Client);
        await Until(() => app.Gate.Snapshot().Queued == 1, "streaming request waits behind it");

        var refused = await Chat(app.Client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(1, app.Ai.Calls);

        app.Ai.Finish(2);
        Assert.Equal(HttpStatusCode.OK, (await plain).StatusCode);
        var response = await streaming;
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Equal(["started", "delta", "completed"], EventNames(sse));
        Assert.Equal(1, app.Ai.MaxRunning);
    }

    [Fact]
    public async Task Stream_WaitingTooLong_EndsWithAnAiBusyErrorEvent_AndNeverCallsTheModel()
    {
        var app = Start(concurrent: 1, queue: 2, wait: 1);
        var running = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");

        var response = await Stream(app.Client);
        var sse = await response.Content.ReadAsStringAsync();

        // The headers were already sent, so the refusal is an event (the HTTP status can no longer change).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["started", "error"], EventNames(sse));
        Assert.Contains("\"code\":\"AI_BUSY\"", sse);
        Assert.Contains("\"retryAfterSeconds\":10", sse);
        Assert.Contains("\"partial\":false", sse);
        Assert.DoesNotContain("AI_TIMEOUT", sse);
        Assert.Equal(1, app.Ai.Calls);
        Assert.Equal((1, 0), app.Gate.Snapshot());

        app.Ai.Finish();
        await running;
        Assert.Equal((0, 0), app.Gate.Snapshot());
    }

    [Fact]
    public async Task Stream_QueueEvents_AreSentOnlyWhenEnabled_AndOnlyToRequestsThatWaited()
    {
        var on = Start(queueEvents: true);
        var running = Chat(on.Client);
        await Until(() => on.Ai.Calls == 1, "first request reaches the model");
        var queued = Stream(on.Client);
        await Until(() => on.Gate.Snapshot().Queued == 1, "stream waits");
        on.Ai.Finish(2);
        await running;
        var waited = await (await queued).Content.ReadAsStringAsync();
        Assert.Equal(["started", "queued", "processing", "delta", "completed"], EventNames(waited));
        Assert.Contains("event: queued\ndata: {\"ahead\":0}", waited);

        on.Ai.Finish();
        var direct = await (await Stream(on.Client)).Content.ReadAsStringAsync();
        Assert.Equal(["started", "delta", "completed"], EventNames(direct)); // a free slot: nothing queued to report

        var off = Start(queueEvents: false);
        var running2 = Chat(off.Client);
        await Until(() => off.Ai.Calls == 1, "first request reaches the model");
        var queued2 = Stream(off.Client);
        await Until(() => off.Gate.Snapshot().Queued == 1, "stream waits");
        off.Ai.Finish(2);
        await running2;
        Assert.Equal(["started", "delta", "completed"], EventNames(await (await queued2).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ClientLeavingWhileQueued_NeverReachesTheModel_AndFreesItsPlace()
    {
        var app = Start(concurrent: 1, queue: 1);
        var running = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");
        using var cts = new CancellationTokenSource();
        var leaving = Chat(app.Client, cts.Token);
        await Until(() => app.Gate.Snapshot().Queued == 1, "second request waits");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        await Until(() => app.Gate.Snapshot().Queued == 0, "its place in the queue is freed");

        Assert.Equal(1, app.Ai.Calls);
        Assert.Equal(1, app.Gate.Metrics.CancelledTotal);
        var replacement = Chat(app.Client); // the freed place is usable at once
        await Until(() => app.Gate.Snapshot().Queued == 1, "a new request takes the freed place");
        app.Ai.Finish(2);
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replacement).StatusCode);
        Assert.Equal(2, app.Ai.Calls); // only the two that stayed
        Assert.Equal((0, 0), app.Gate.Snapshot());
    }

    [Fact]
    public async Task ClientLeavingWhileProcessing_StopsTheModelCall_AndTheNextRequestIsServed()
    {
        var app = Start(concurrent: 1, queue: 2);
        using var cts = new CancellationTokenSource();
        var leaving = Chat(app.Client, cts.Token);
        await Until(() => app.Ai.Calls == 1, "request reaches the model");
        var next = Chat(app.Client);
        await Until(() => app.Gate.Snapshot().Queued == 1, "next request waits");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        await Until(() => app.Ai.CancelledCalls == 1, "cancellation reaches the model call");
        await Until(() => app.Ai.Calls == 2, "the waiting request now gets the slot");

        app.Ai.Finish();
        Assert.Equal(HttpStatusCode.OK, (await next).StatusCode);
        Assert.Equal((0, 0), app.Gate.Snapshot());
    }

    [Fact]
    public async Task ProviderError_ReleasesTheSlotAndQuota()
    {
        var app = Start(concurrent: 1, queue: 0, perUser: 1);
        app.Ai.FailNext = true;

        var failed = await Chat(app.Client);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal((0, 0), app.Gate.Snapshot());

        app.Ai.Finish();
        Assert.Equal(HttpStatusCode.OK, (await Chat(app.Client)).StatusCode);
    }

    [Fact]
    public async Task ManyParallelRequests_NeverExceedTheLimit_AndAllEndInAnAllowedStatus()
    {
        var app = Start(concurrent: 2, queue: 5, wait: 30);
        var requests = Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? Chat(app.Client) : Stream(app.Client)).ToList();

        // 2 in the model + 5 waiting = 7 accepted at most; the rest were refused at once.
        await Until(() => app.Gate.Snapshot() == (2, 5) && app.Gate.Metrics.RejectedQueueFullTotal == 23, "queue fills and the rest are refused");
        app.Ai.Finish(30);
        var responses = await Task.WhenAll(requests);

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.All(statuses, s => Assert.Contains(s, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }));
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(23, statuses.Count(s => s == HttpStatusCode.ServiceUnavailable));
        Assert.True(app.Ai.MaxRunning <= 2, $"{app.Ai.MaxRunning} calls at once, limit 2");
        Assert.Equal(7, app.Ai.Calls);
        await Until(() => app.Gate.Snapshot() == (0, 0), "everything is released");
    }

    [Fact]
    public async Task Logs_AndErrors_NeverContainTheQuestion()
    {
        var app = Start(concurrent: 1, queue: 0);
        var running = Chat(app.Client);
        await Until(() => app.Ai.Calls == 1, "first request reaches the model");

        var refused = await app.Client.PostAsync("/api/chat", Body("private-question-sentinel"));

        Check.NotLeaked(await refused.Content.ReadAsStringAsync(), "private-question-sentinel");
        app.Ai.Finish();
        await running;
    }
}

/// <summary>BE-008 with real sessions: authorization comes first and the quota follows the verified identity only.</summary>
public sealed class ChatAdmissionIdentityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private (SecurityHost Host, GatedAi Ai, ChatAdmissionQueue Gate) Create(int concurrent = 1, int queue = 5, int perUser = 1)
    {
        var ai = new GatedAi();
        var host = SecurityHost.Create(factory);
        host = host with
        {
            Factory = host.Factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ChatQueue:MaxConcurrentRequests"] = concurrent.ToString(),
                    ["ChatQueue:MaxQueueLength"] = queue.ToString(),
                    ["ChatQueue:MaxPendingPerUser"] = perUser.ToString()
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAIService>(ai);
                    services.AddSingleton<IStreamingAIService>(ai);
                });
            })
        };
        host.Users.Put("u-1");
        host.Users.Put("u-2");
        return (host, ai, host.Factory.Services.GetRequiredService<ChatAdmissionQueue>());
    }

    private static async Task<string> SignIn(SecurityHost host, string userId)
    {
        var response = await host.Raw().PostAsync($"/api/__test/signin/{userId}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(BcAuthentication.CookieName + "=", StringComparison.Ordinal)).Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> Post(SecurityHost host, string? cookie, string path = "/api/chat", params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // A body that claims to be another user must change nothing.
            Content = new StringContent("""{"message":"hello","userId":"u-2"}""", Encoding.UTF8, "application/json")
        };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        return host.Raw().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/chat/stream")]
    public async Task NotSignedIn_IsRejectedBeforeTheQueueOrTheModelIsTouched(string path)
    {
        var (host, ai, gate) = Create();

        var response = await Post(host, cookie: null, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, ai.Calls);
        Assert.Equal(0, gate.Metrics.AdmittedTotal);
        Assert.Equal(0, gate.Metrics.RejectedQueueFullTotal + gate.Metrics.RejectedUserQuotaTotal);
    }

    [Fact]
    public async Task Quota_IsPerVerifiedUser_AndASpoofedIdentityDoesNotEscapeIt()
    {
        var (host, ai, gate) = Create(concurrent: 1, queue: 5, perUser: 1);
        var alice = await SignIn(host, "u-1");
        var bob = await SignIn(host, "u-2");
        var first = Post(host, alice);
        await Until(() => ai.Calls == 1, "alice's request reaches the model");

        // Alice again, claiming to be bob in a header and in the body: still alice's quota.
        var spoofed = await Post(host, alice, "/api/chat", ("X-User-Id", "u-2"), ("X-Forwarded-For", "10.9.9.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);

        // Bob is independent: accepted, waiting for the slot (not refused).
        var other = Post(host, bob);
        await Until(() => gate.Snapshot().Queued == 1, "bob waits in line");

        ai.Finish(2);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other).StatusCode);
        Assert.Equal(1, gate.Metrics.RejectedUserQuotaTotal);
        Assert.Equal((0, 0), gate.Snapshot());
    }

    [Fact]
    public async Task AuthenticationOff_EveryoneSharesOneQuota_SoThereIsNoWayAroundIt()
    {
        var ai = new GatedAi();
        var app = factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama();
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatQueue:MaxPendingPerUser"] = "1"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAIService>(ai);
                services.AddSingleton<IStreamingAIService>(ai);
            });
        });
        var client = app.CreateClient();
        var first = client.PostAsync("/api/chat", new StringContent("""{"message":"a"}""", Encoding.UTF8, "application/json"));
        await Until(() => ai.Calls == 1, "first request reaches the model");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent("""{"message":"b","userId":"someone-new"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-User-Id", "someone-new");
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        ai.Finish();
        await first;
    }
}
