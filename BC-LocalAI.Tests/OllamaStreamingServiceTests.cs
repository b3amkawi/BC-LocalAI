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
        var callbacks = 0;
        var bothActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler((_, _) =>
        {
            var answer = Interlocked.Increment(ref number) == 1 ? "first" : "second";
            return Task.FromResult(Response(Record($"{{\"answer\":\"{answer}\"}}") + "\n" + Final() + "\n"));
        });
        var service = Create(handler);
        var first = new List<string>();
        var second = new List<string>();

        async Task Capture(string delta, List<string> target)
        {
            if (Interlocked.Increment(ref callbacks) == 2) bothActive.TrySetResult();
            await bothActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            target.Add(delta);
        }

        await Task.WhenAll(
            service.StreamResponseAsync("one", (delta, _) => Capture(delta, first)),
            service.StreamResponseAsync("two", (delta, _) => Capture(delta, second)));

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

    [Theory]
    [InlineData("text/html", null)]
    [InlineData("text/plain", null)]
    [InlineData("application/x-ndjson", "iso-8859-1")]
    public async Task WrongMediaTypeOrCharset_IsRejected(string mediaType, string? charset)
    {
        var response = Response(Record("{\"answer\":\"safe\"}") + "\n" + Final() + "\n");
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = charset };
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(FakeHandler.Returning(response))
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(206)]
    public async Task OnlyHttp200CanComplete(int status)
    {
        var response = Response(Record("{\"answer\":\"safe\"}") + "\n" + Final() + "\n");
        response.StatusCode = (HttpStatusCode)status;
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(FakeHandler.Returning(response))
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_PROVIDER_ERROR", error.Code);
    }

    [Theory]
    [InlineData("\u00a0{\"answer\":\"safe\"}")]
    [InlineData("{\"answer\":\"safe\"}\u000b")]
    public async Task NonJsonWhitespace_IsInvalid(string payload)
    {
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(FakeHandler.Returning(
            Response(Record(payload) + "\n" + Final() + "\n")))
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code);
    }

    [Fact]
    public async Task JsonAllowedControlCharacter_IsPreserved()
    {
        var deltas = new List<string>();
        await Create(FakeHandler.Returning(Response(Record("{\"answer\":\"text\u007f\"}") + "\n" + Final() + "\n")))
            .StreamResponseAsync("hello", (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });
        Assert.Equal("text\u007f", string.Concat(deltas));
    }

    [Fact]
    public async Task TransportExceptions_DoNotAppearInLogs()
    {
        var logs = new CapturingLoggerProvider();
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("secret-prompt credential-sentinel"));
        var service = new OllamaStreamingService(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") },
            Options.Create(new OllamaOptions()), logs.CreateLogger<OllamaStreamingService>());
        await Assert.ThrowsAsync<AiServiceException>(() => service.StreamResponseAsync("private", (_, _) => Task.CompletedTask));
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
        Check.NotLeaked(logs.Text, "secret-prompt", "credential-sentinel", "private");
    }

    [Fact]
    public async Task OptionalResponseOnFinalRecord_CanBeOmitted()
    {
        var body = Record("{\"answer\":\"safe\"}") + "\n" +
            "{\"model\":\"qwen3:4b\",\"done\":true,\"done_reason\":\"stop\"}\n";
        var result = await Create(FakeHandler.Returning(Response(body))).StreamResponseAsync("hello", (_, _) => Task.CompletedTask);
        Assert.Equal("stop", result.FinishReason);
    }

    [Theory]
    [InlineData("{\"answer\":\"\"}")]
    [InlineData("{\"answer\":\"   \"}")]
    [InlineData("{\"answer\":42}")]
    [InlineData("{\"answer\":\"ok\",\"extra\":1}")]
    [InlineData("{\"answer\":\"first\",\"answer\":\"second\"}")]
    [InlineData("{\"answer\":\"\\q\"}")]
    [InlineData("{\"answer\":\"\\uD800\"}")]
    [InlineData("{\"answer\":\"\\uDC00\"}")]
    [InlineData("{\"answer\":\"unfinished")]
    [InlineData("[]")]
    [InlineData("plain thinking text")]
    public async Task InvalidAnswerCannotComplete(string payload)
    {
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(FakeHandler.Returning(
            Response(Record(payload) + "\n" + Final() + "\n")))
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code);
    }

    [Fact]
    public async Task ActualUtf8BytesAndSplitUnicodeEscapes_ArePreserved()
    {
        const string answer = "ไทย 😀 \"quoted\" \\ path\nnext";
        var payload = JsonSerializer.Serialize(new { answer });
        var records = payload.Select(character => JsonSerializer.Serialize(new
        {
            model = "qwen3:4b", response = character.ToString(), done = false
        }));
        // Raw Thai/emoji in the NDJSON metadata force the reader to split multibyte UTF-8 too.
        var rawUtf8 = "{\"model\":\"qwen3:4b\",\"response\":\"\",\"done\":false,\"context_note\":\"ไทย 😀\"}\n";
        var body = rawUtf8 + string.Join('\n', records) + "\n" + Final() + "\n";
        var deltas = new List<string>();
        await Create(FakeHandler.Returning(Response(new ChunkedReadStream(Encoding.UTF8.GetBytes(body), 1))))
            .StreamResponseAsync("hello", (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });
        Assert.Equal(answer, string.Concat(deltas));
    }

    [Theory]
    [InlineData("{\"model\":\"qwen3:4b\",\"done\":\"false\",\"response\":\"\"}")]
    [InlineData("{\"model\":\"qwen3:4b\",\"done\":false,\"response\":null}")]
    [InlineData("{\"model\":\"qwen3:4b\",\"done\":false,\"thinking\":false}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"model\":\"other\",\"model\":\"qwen3:4b\",\"done\":false,\"response\":\"\"}")]
    public async Task MalformedKnownFields_AreInvalid(string record)
    {
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(FakeHandler.Returning(Response(record + "\n")))
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code);
    }

    [Fact]
    public async Task InvalidUtf8_MapsToInvalidResponse_AndDisposesContent()
    {
        var content = new TrackedContent(new ChunkedReadStream([0xff, 0xfe, 0x0a]), "application/x-ndjson");
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(handler)
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task UnboundedLine_IsStoppedAtByteLimitWithoutReadingEntireBody()
    {
        var stream = new EndlessStream();
        var content = new TrackedContent(stream, "application/x-ndjson");
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Create(
            FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }), maxResponseBytes: 4096)
            .StreamResponseAsync("hello", (_, _) => Task.CompletedTask));
        Assert.Equal("AI_RESPONSE_TOO_LARGE", error.Code);
        Assert.Equal(4097, stream.BytesRead);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task LargeFragment_DoesNotFlushOneEventPerCharacter()
    {
        var answer = new string('ก', 1000);
        var payload = JsonSerializer.Serialize(new { answer });
        var deltas = new List<string>();
        await Create(FakeHandler.Returning(Response(Record(payload) + "\n" + Final() + "\n")))
            .StreamResponseAsync("hello", (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });
        Assert.Equal(answer, string.Concat(deltas));
        Assert.Equal(2, deltas.Count); // fragment plus final marker-sized tail, instead of 1000 SSE writes
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
