using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace BC_LocalAI.Tests;

/// <summary>Exercises the shipped streaming HttpClient over real loopback TCP to a controlled Kestrel provider.</summary>
public sealed class StreamingTransportTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Redirect_IsNeverFollowed()
    {
        var trapCalls = 0;
        await using var provider = await StartProvider(async context =>
        {
            context.Response.StatusCode = 307;
            context.Response.Headers.Location = "/trap";
            await context.Response.CompleteAsync();
        }, () => Interlocked.Increment(ref trapCalls));
        using var client = CreateClient(provider);

        var response = await client.PostAsJsonAsync("/api/chat/stream", new { message = "private question" });
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Contains("AI_PROVIDER_ERROR", sse);
        Assert.DoesNotContain("event: completed", sse);
        Assert.Equal(0, trapCalls);
    }

    [Fact]
    public async Task StartedAndDeltaArriveBeforeUpstreamCompletes_AndDisconnectStopsProvider()
    {
        var received = Signal();
        var release = Signal();
        var cancelled = Signal();
        await using var provider = await StartProvider(async context =>
        {
            using var registration = context.RequestAborted.Register(() => cancelled.TrySetResult());
            received.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            context.Response.ContentType = "application/x-ndjson";
            var record = JsonSerializer.Serialize(new
            {
                model = "qwen3:4b", response = "{\"answer\":\"streaming answer still being generated", done = false
            });
            await context.Response.WriteAsync(record + "\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });
        using var client = CreateClient(provider);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(new { message = "hello" })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
        using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);

        Assert.Equal("event: started", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("qwen3:4b", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal("event: delta", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("text", await reader.ReadLineAsync());

        response.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TimeoutClosesUpstream_AndReturnsSingleError()
    {
        var cancelled = Signal();
        await using var provider = await StartProvider(async context =>
        {
            using var registration = context.RequestAborted.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });
        using var client = CreateClient(provider, timeoutSeconds: "1");

        var response = await client.PostAsJsonAsync("/api/chat/stream", new { message = "hello" }).WaitAsync(TimeSpan.FromSeconds(10));
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("AI_TIMEOUT", sse);
        Assert.Equal(1, sse.Split("event: error").Length - 1);
        Assert.DoesNotContain("event: completed", sse);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private HttpClient CreateClient(WebApplication provider, string timeoutSeconds = "180") =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.PinOllama(baseUrl: provider.Urls.Single(), timeoutSeconds: timeoutSeconds);
        }).CreateClient();

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<WebApplication> StartProvider(RequestDelegate generate, Action? trap = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapPost("/api/generate", generate);
        app.MapPost("/trap", () => { trap?.Invoke(); return Results.Ok(); });
        await app.StartAsync();
        return app;
    }
}
