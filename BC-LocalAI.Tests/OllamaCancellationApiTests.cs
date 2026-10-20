using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace BC_LocalAI.Tests;

/// <summary>
/// AI-002 contract: the backend must tell "the user cancelled / closed the connection" apart from
/// "Ollama did not answer in time". Only the second one is a provider timeout (HTTP 504).
/// </summary>
public sealed class OllamaCancellationApiTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private HttpClient CreateClient(FakeHandler handler, CapturingLoggerProvider logs, string timeoutSeconds = "180") =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama(timeoutSeconds: timeoutSeconds);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }).CreateClientWithOllama(handler, "Production", pinOllama: false);

    private static StringContent Body() =>
        new("""{"message":"hello"}""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task ClientDisconnect_CancelsOllamaCall_AndIsNotAProviderTimeout()
    {
        var logs = new CapturingLoggerProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCallCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, token) =>
        {
            token.Register(() => providerCallCancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // an Ollama that never answers
            return FakeHandler.OllamaResponse("{\"answer\":\"never\"}");
        });
        var client = CreateClient(handler, logs);
        using var userCancels = new CancellationTokenSource();

        var request = client.PostAsync("/api/chat", Body(), userCancels.Token);
        await started.Task.WaitAsync(Patience);
        await userCancels.CancelAsync();

        // The user's cancellation must reach the call to Ollama promptly (so GPU work is not kept running
        // for nobody). If it did not, only HttpClient's own 180 s timeout would end the call, so wait briefly.
        await providerCallCancelled.Task.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
        await logs.WaitForAsync("Request was cancelled by the client.", Patience);

        Check.NotLeaked(logs.Text, "did not answer in time", "AI_TIMEOUT");
    }

    [Fact]
    public async Task ProviderTimeout_Returns504_AndIsNotLoggedAsClientCancellation()
    {
        var logs = new CapturingLoggerProvider();
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token); // only HttpClient's own timeout can end this
            return FakeHandler.OllamaResponse("{\"answer\":\"never\"}");
        });
        var client = CreateClient(handler, logs, timeoutSeconds: "1");

        var clock = Stopwatch.StartNew();
        var response = await client.PostAsync("/api/chat", Body()).WaitAsync(Patience); // fails fast if no timeout applies
        clock.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("AI_TIMEOUT", body!.Error.Code);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 8); // the configured 1 s timeout, not the 100 s HttpClient default
        await logs.WaitForAsync("did not answer in time", Patience);
        Check.NotLeaked(logs.Text, "cancelled by the client");
    }

    [Fact]
    public async Task CompletedRequest_LogsNeitherTimeoutNorCancellation()
    {
        var logs = new CapturingLoggerProvider();
        var client = CreateClient(FakeHandler.OllamaOk(), logs);

        var response = await client.PostAsync("/api/chat", Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Check.NotLeaked(logs.Text, "did not answer in time", "cancelled by the client");
    }
}

internal sealed record LogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public string Text => string.Join(Environment.NewLine, _entries.Select(e => $"{e.Level}: {e.Message}"));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public ILogger<T> CreateLogger<T>() => new CapturingLogger<T>(_entries);

    public void Dispose()
    {
    }

    public async Task WaitForAsync(string fragment, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!Text.Contains(fragment, StringComparison.Ordinal))
        {
            if (deadline.Elapsed > timeout)
            {
                throw new Xunit.Sdk.XunitException($"Log never contained '{fragment}'. Logs were: {Text}");
            }

            await Task.Delay(25);
        }
    }

    private class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    fields[pair.Key] = pair.Value;
                }
            }

            entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), fields, exception));
        }
    }

    private sealed class CapturingLogger<T>(ConcurrentQueue<LogEntry> entries)
        : CapturingLogger(entries), ILogger<T>;
}
