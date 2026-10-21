using System.Diagnostics;
using System.Net;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services;

namespace BC_LocalAI.Tests;

/// <summary>
/// BE-003: timeout policy, cancellation propagation and release of resources, at the service level
/// (the HTTP level is in OllamaCancellationApiTests).
/// </summary>
public sealed class OllamaTimeoutCancellationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly OllamaOptions OneSecond = new() { TimeoutSeconds = 1 };

    private static byte[] GoodBody() => OllamaBodies.Bytes("{\"answer\":\"ok\"}");

    // ---------------------------------------------------------------- the deadline

    [Fact]
    public async Task Deadline_EndsAProviderThatNeverAnswers_AsATimeout_NotAnUnavailable()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        });
        var clock = Stopwatch.StartNew();

        var failure = await Svc.FailureOf(Svc.Create(handler, OneSecond)).WaitAsync(Patience);
        clock.Stop();

        Assert.Equal("AI_TIMEOUT", failure.Code);
        Assert.Equal(HttpStatusCode.GatewayTimeout, failure.StatusCode);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.8, 5); // the configured 1 s, not HttpClient's 100 s default
    }

    [Fact]
    public async Task Deadline_AlsoCoversReadingTheBody()
    {
        var stalling = new StallingStream(System.Text.Encoding.UTF8.GetBytes("{\"model\":\"qwen3:4b\","));
        var content = new TrackedContent(stalling);
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var failure = await Svc.FailureOf(Svc.Create(handler, OneSecond)).WaitAsync(Patience);

        Assert.Equal("AI_TIMEOUT", failure.Code);
        await stalling.TokenCancelled.Task.WaitAsync(Patience); // the read was really cancelled
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Deadline_CountsTheWholeExchange_NotEachRead()
    {
        // 30 bytes, one every 100 ms: every read is quick, but the whole body takes about 3 s (> 1 s deadline).
        var body = System.Text.Encoding.UTF8.GetBytes(new string(' ', 30));
        var content = new TrackedContent(new DripStream(body, TimeSpan.FromMilliseconds(100)));
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var failure = await Svc.FailureOf(Svc.Create(handler, OneSecond)).WaitAsync(Patience);

        Assert.Equal("AI_TIMEOUT", failure.Code);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Deadline_DoesNotCutInWhenTheAnswerArrivesInTime()
    {
        // The positive control is an already-available response, not a wall-clock scheduling race. The three negative
        // deadline tests above still enforce the configured one-second limit, including stalled and drip-fed bodies.
        var handler = FakeHandler.Returning(OllamaBodies.Response(GoodBody()).Response);

        var result = await Svc.Create(handler, OneSecond).GenerateResponseAsync("hi");

        Assert.Equal("ok", result.Response);
    }

    [Fact]
    public async Task HttpClientTimeout_IfItEverFires_IsStillATimeout()
    {
        // e.g. a handler stack that has its own timeout: TaskCanceledException caused by TimeoutException.
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));

        var failure = await Svc.FailureOf(Svc.Create(handler));

        Assert.Equal("AI_TIMEOUT", failure.Code);
    }

    // ---------------------------------------------------------------- cancellation by the caller

    [Fact]
    public async Task CallerCancel_WhileWaitingForTheProvider_StopsTheHttpCall_AndIsNotAnAiFailure()
    {
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, token) =>
        {
            token.Register(() => providerCancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        });
        using var caller = new CancellationTokenSource();
        var task = Svc.Create(handler).GenerateResponseAsync("hi", caller.Token);
        await started.Task.WaitAsync(Patience);

        await caller.CancelAsync();

        await providerCancelled.Task.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Patience));
    }

    [Fact]
    public async Task CallerCancel_WhileReadingTheBody_StopsTheRead_ReleasesTheResponse_AndIsNotAnAiFailure()
    {
        var stalling = new StallingStream(System.Text.Encoding.UTF8.GetBytes("{\"model\":\"qwen3:4b\","));
        var content = new TrackedContent(stalling);
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var caller = new CancellationTokenSource();
        var task = Svc.Create(handler).GenerateResponseAsync("hi", caller.Token);
        await stalling.Stalled.Task.WaitAsync(Patience);

        await caller.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Patience));
        Assert.IsNotType<AiServiceException>(thrown);
        await stalling.TokenCancelled.Task.WaitAsync(Patience);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task CallerAlreadyCancelled_NeverCallsTheProvider()
    {
        var handler = FakeHandler.OllamaOk();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Svc.Create(handler).GenerateResponseAsync("hi", caller.Token));

        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task SuccessIsNeverReturned_ToACallerThatCancelledMeanwhile()
    {
        using var caller = new CancellationTokenSource();
        // The whole valid body arrives, and the caller goes away exactly when the body ends: everything the service
        // needs is there, so only the final check stops it from returning a success to a caller that has left.
        var content = new TrackedContent(new CancelAtEndStream(GoodBody(), caller));
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var thrown = await Record.ExceptionAsync(() => Svc.Create(handler).GenerateResponseAsync("hi", caller.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.True(caller.IsCancellationRequested);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task WithoutACancellation_TheSameBodyStillSucceeds()
    {
        using var caller = new CancellationTokenSource(); // never cancelled
        var content = new TrackedContent(new CancelAtEndStream(GoodBody(), new CancellationTokenSource()));
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await Svc.Create(handler).GenerateResponseAsync("hi", caller.Token);

        Assert.Equal("ok", result.Response);
    }

    [Fact]
    public async Task ATimeoutIsNotACancellation_AndACancellationIsNotATimeout()
    {
        // timeout: caller still waiting -> AiServiceException
        var hang = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        var timeout = await Record.ExceptionAsync(() => Svc.Create(hang, OneSecond).GenerateResponseAsync("hi").WaitAsync(Patience));
        Assert.IsType<AiServiceException>(timeout);

        // cancellation: caller left -> OperationCanceledException, never AiServiceException
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var cancelled = await Record.ExceptionAsync(() => Svc.Create(hang, new OllamaOptions { TimeoutSeconds = 60 }).GenerateResponseAsync("hi", caller.Token).WaitAsync(Patience));
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        Assert.IsNotType<AiServiceException>(cancelled);
    }

    // ---------------------------------------------------------------- the response is always released

    public static TheoryData<string> Scenarios =>
    [
        "success", "http-500", "http-404", "not-json", "invalid-answer", "too-large-tokens", "too-large-bytes"
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TheResponseIsReleased_OnEveryPath(string scenario)
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var (response, content) = scenario switch
        {
            "success" => OllamaBodies.Response(GoodBody()),
            "http-500" => OllamaBodies.Response(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"boom\"}"), HttpStatusCode.InternalServerError),
            "http-404" => OllamaBodies.Response(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"model not found\"}"), HttpStatusCode.NotFound),
            "not-json" => OllamaBodies.Response(System.Text.Encoding.UTF8.GetBytes("<html>bad gateway</html>")),
            "invalid-answer" => OllamaBodies.Response(OllamaBodies.Bytes("{\"wrong\":1}")),
            "too-large-tokens" => OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"cut", doneReason: "length")),
            "too-large-bytes" => OllamaBodies.Response(new byte[options.MaxResponseBytes + 10]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        await Record.ExceptionAsync(() => Svc.Create(FakeHandler.Returning(response), options).GenerateResponseAsync("hi"));

        Assert.True(content.Disposed, scenario);
    }

    [Fact]
    public async Task ConnectionFailure_IsUnavailable_NotATimeout()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));

        var failure = await Svc.FailureOf(Svc.Create(handler));

        Assert.Equal("AI_UNAVAILABLE", failure.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    }

    [Fact]
    public async Task ConnectionResetWhileReadingTheBody_IsUnavailable_NotAnUnhandledError()
    {
        var content = new TrackedContent(new ThrowingStream(new IOException("connection reset")));
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var failure = await Svc.FailureOf(Svc.Create(handler));

        Assert.Equal("AI_UNAVAILABLE", failure.Code);
        Assert.True(content.Disposed);
    }

    private sealed class ThrowingStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw exception;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
