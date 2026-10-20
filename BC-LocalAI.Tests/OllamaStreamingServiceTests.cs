using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

public sealed class OllamaStreamingServiceTests
{
    [Fact]
    public async Task ValidFragmentedNdjson_DecodesOnlyAnswer_WithUnicodeAndEscapes()
    {
        var lines = string.Join('\n',
            Record("{\"ans"),
            Record("wer\":\"สวัสดี "),
            Record("😀\\nโลก\"}"),
            Final()) + "\n";
        var bytes = Encoding.UTF8.GetBytes(lines);
        var handler = FakeHandler.Returning(Response(new ChunkedReadStream(bytes, 1, 2, 3)));
        var deltas = new List<string>();

        var completion = await Create(handler).StreamResponseAsync(
            "hello",
            (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });

        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(7, completion.EvalCount);
        Assert.Equal("สวัสดี 😀\nโลก", string.Concat(deltas));
        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("object", request.RootElement.GetProperty("format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("length", "AI_RESPONSE_TOO_LARGE")]
    [InlineData("load", "AI_INVALID_RESPONSE")]
    public async Task NonStopCompletion_IsAnError(string reason, string code)
    {
        var body = Record("{\"answer\":\"partial") + "\n" + Final(reason) + "\n";
        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(body))).StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task EofWithoutDone_IsInvalidAndNeverCompletes()
    {
        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(Record("{\"answer\":\"partial"))))
                .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task ProviderErrorRecord_DoesNotLeakProviderDetail()
    {
        var handler = FakeHandler.Returning(Response("{\"error\":\"secret endpoint detail\"}\n"));

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(handler).StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_PROVIDER_ERROR", exception.Code);
        Check.NotLeaked(exception.Message, "secret endpoint detail");
    }

    [Fact]
    public async Task ThinkingMarkerSplitAcrossRecords_IsRejectedBeforeMarkerIsEmitted()
    {
        var body = Record("{\"answer\":\"safe <thi") + "\n" + Record("nk>secret\"}") + "\n" + Final() + "\n";
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(body))).StreamResponseAsync(
                "hello", (delta, _) => { deltas.Add(delta); return Task.CompletedTask; }));

        Assert.Equal("AI_INVALID_RESPONSE", exception.Code);
        Assert.DoesNotContain("<think>", string.Concat(deltas), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThinkingField_IsIgnoredAndNeverAppearsInDelta()
    {
        var thinking = JsonSerializer.Serialize(new
        {
            model = "qwen3:4b", response = "", thinking = "secret chain of thought", done = false
        });
        var body = thinking + "\n" + Record("{\"answer\":\"safe\"}") + "\n" + Final() + "\n";
        var deltas = new List<string>();

        await Create(FakeHandler.Returning(Response(body))).StreamResponseAsync(
            "hello", (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });

        Assert.Equal("safe", string.Concat(deltas));
        Assert.DoesNotContain("secret", string.Concat(deltas));
    }

    [Theory]
    [InlineData("qwen3:8b")]
    [InlineData("")]
    public async Task WrongOrMissingModel_IsInvalid(string model)
    {
        var body = Record("{\"answer\":\"safe\"}", model) + "\n" + Final() + "\n";

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(body))).StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task DataAfterDone_IsInvalid()
    {
        var body = Record("{\"answer\":\"safe\"}") + "\n" + Final() + "\n" + Record("") + "\n";

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(body))).StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task CallerCancellation_ReachesProviderRead()
    {
        var stream = new StallingStream(Encoding.UTF8.GetBytes(Record("{\"answer\":\"started ") + "\n"));
        var service = Create(FakeHandler.Returning(Response(stream)));
        using var cancelled = new CancellationTokenSource();
        var call = service.StreamResponseAsync("hello", (_, _) => Task.CompletedTask, cancelled.Token);
        await stream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await stream.TokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentCalls_DoNotShareDecoderState()
    {
        var number = 0;
        var handler = new FakeHandler((_, _) =>
        {
            var answer = Interlocked.Increment(ref number) == 1 ? "first" : "second";
            return Task.FromResult(Response(Record($"{{\"answer\":\"{answer}\"}}") + "\n" + Final() + "\n"));
        });
        var service = Create(handler);
        var first = new List<string>();
        var second = new List<string>();

        await Task.WhenAll(
            service.StreamResponseAsync("one", (delta, _) => { first.Add(delta); return Task.CompletedTask; }),
            service.StreamResponseAsync("two", (delta, _) => { second.Add(delta); return Task.CompletedTask; }));

        Assert.Equal("first", string.Concat(first));
        Assert.Equal("second", string.Concat(second));
    }

    [Fact]
    public async Task TotalTimeout_IsReportedAsAiTimeout()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Final() + "\n");
        });

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(handler, timeoutSeconds: 1).StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_TIMEOUT", exception.Code);
    }

    [Fact]
    public async Task ResponseByteLimit_IsEnforcedDuringStream()
    {
        var body = Record("{\"answer\":\"this is larger than the deliberately tiny test limit\"}") + "\n" + Final() + "\n";

        var exception = await Assert.ThrowsAsync<AiServiceException>(() =>
            Create(FakeHandler.Returning(Response(body)), maxResponseBytes: 32)
                .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));

        Assert.Equal("AI_RESPONSE_TOO_LARGE", exception.Code);
    }

    private static OllamaStreamingService Create(
        FakeHandler handler,
        int timeoutSeconds = 180,
        int maxResponseBytes = 1_048_576) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") },
            Options.Create(new OllamaOptions
            {
                TimeoutSeconds = timeoutSeconds,
                MaxResponseBytes = maxResponseBytes
            }),
            NullLogger<OllamaStreamingService>.Instance);

    private static HttpResponseMessage Response(string body) => Response(new MemoryStream(Encoding.UTF8.GetBytes(body)));

    private static HttpResponseMessage Response(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new TrackedContent(body, "application/x-ndjson")
    };

    private static string Record(string response, string model = "qwen3:4b") =>
        JsonSerializer.Serialize(new { model, response, done = false });

    private static string Final(string reason = "stop") =>
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = "", done = true, done_reason = reason, eval_count = 7 });
}
