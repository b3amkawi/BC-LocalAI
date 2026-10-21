using System.Text;
using BC_LocalAI.Configuration;
using BC_LocalAI.Controllers;
using BC_LocalAI.Models;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

public sealed class ChatStreamControllerTests
{
    [Fact]
    public async Task CancellationWithWritableResponse_EmitsCancelledOnce()
    {
        using var caller = new CancellationTokenSource();
        var logs = new CapturingLoggerProvider();
        var controller = Create(new DelegateService((_, _, _) =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        }), new MemoryStream(), logs);

        await controller.PostAsync(new ChatRequest { Message = "cancel" }, caller.Token);

        var text = Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());
        Assert.Contains("event: started", text);
        Assert.Equal(1, text.Split("event: cancelled").Length - 1);
        Check.NotLeaked(text, "event: completed", "event: error");
    }

    [Theory]
    [InlineData("started")]
    [InlineData("delta")]
    [InlineData("completed")]
    public async Task DownstreamWriteFailure_DoesNotAppendAnotherEvent(string failAt)
    {
        var stream = new EventFailingStream(failAt);
        var calls = 0;
        var logs = new CapturingLoggerProvider();
        var controller = Create(new DelegateService(async (_, delta, token) =>
        {
            calls++;
            await delta("answer", token);
            return new AiStreamCompletion("stop", 1);
        }), stream, logs);

        await controller.PostAsync(new ChatRequest { Message = "hi" }, CancellationToken.None);

        Assert.Equal(failAt == "started" ? 0 : 1, calls);
        Assert.Equal(1, stream.Failures);
        Assert.DoesNotContain("event: error", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.DoesNotContain(logs.Entries, entry => entry.Exception is not null);
    }

    [Fact]
    public async Task UnexpectedFailure_LogsOnlyTypeAndReturnsSafeTerminalError()
    {
        var logs = new CapturingLoggerProvider();
        var controller = Create(new DelegateService((_, _, _) => throw new InvalidOperationException("credential-sentinel")),
            new MemoryStream(), logs);

        await controller.PostAsync(new ChatRequest { Message = "private-prompt" }, CancellationToken.None);

        var text = Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());
        Assert.Contains("INTERNAL_ERROR", text);
        Assert.Equal(1, text.Split("event: error").Length - 1);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
        Check.NotLeaked(text + logs.Text, "credential-sentinel", "private-prompt");
    }

    private static ChatStreamController Create(IStreamingAIService service, Stream stream, CapturingLoggerProvider logs)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = stream;
        return new ChatStreamController(service, SessionGuards.Off(), ChatTurns.Off(), Options.Create(new BC_LocalAI.Configuration.ChatHistoryOptions()), TimeProvider.System, Options.Create(new OllamaOptions()), Admissions.Open(), logs.CreateLogger<ChatStreamController>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class DelegateService(
        Func<string, Func<string, CancellationToken, Task>, CancellationToken, Task<AiStreamCompletion>> run) : IStreamingAIService
    {
        public Task<AiStreamCompletion> StreamResponseAsync(string message, Func<string, CancellationToken, Task> onDelta,
            CancellationToken cancellationToken = default) => run(message, onDelta, cancellationToken);
    }

    private sealed class EventFailingStream(string failAt) : MemoryStream
    {
        public int Failures { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(buffer.Span).Contains($"event: {failAt}", StringComparison.Ordinal))
            {
                Failures++;
                throw new IOException("downstream is closed");
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
