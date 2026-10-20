using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests;

public sealed class ChatStreamApiTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task ValidRequest_ReturnsOrderedSseContract()
    {
        var provider = string.Join('\n',
            Record("{\"answer\":\"กร"),
            Record("ุงเทพ 😀\"}"),
            Final()) + "\n";
        var client = factory.CreateClientWithStreamingOllama(FakeHandler.Returning(Response(provider)));

        var response = await client.PostAsync("/api/chat/stream", Body("สวัสดี"));
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        AssertSseOrder(sse, "started", "delta", "completed");
        Assert.Equal("กรุงเทพ 😀", ReadDeltaText(sse));
        Assert.DoesNotContain("{\\\"answer", sse);
    }

    [Fact]
    public async Task ProviderFailureAfterStarted_ReturnsTerminalSafeErrorEvent()
    {
        var client = factory.CreateClientWithStreamingOllama(
            FakeHandler.Returning(Response("{\"error\":\"secret provider path\"}\n")),
            environment: "Production");

        var response = await client.PostAsync("/api/chat/stream", Body("hello"));
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSseOrder(sse, "started", "error");
        Assert.Contains("AI_PROVIDER_ERROR", sse);
        Assert.Contains("\"partial\":false", sse);
        Check.NotLeaked(sse, "secret provider path", "11434", "api/generate");
        Assert.DoesNotContain("event: completed", sse);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":\"  \"}")]
    [InlineData("{bad")]
    public async Task InvalidRequest_Returns400JsonBeforeSseStarts(string json)
    {
        var handler = FakeHandler.Returning(Response(Final() + "\n"));
        var client = factory.CreateClientWithStreamingOllama(handler);

        var response = await client.PostAsync("/api/chat/stream", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task LengthAfterDelta_ReturnsPartialErrorAndNoCompleted()
    {
        var provider = Record("{\"answer\":\"partial answer") + "\n" + Final("length") + "\n";
        var client = factory.CreateClientWithStreamingOllama(FakeHandler.Returning(Response(provider)));

        var response = await client.PostAsync("/api/chat/stream", Body("long answer"));
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: delta", sse);
        Assert.Contains("AI_RESPONSE_TOO_LARGE", sse);
        Assert.Contains("\"partial\":true", sse);
        Assert.DoesNotContain("event: completed", sse);
    }

    private static StringContent Body(string message) =>
        new(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new TrackedContent(new MemoryStream(Encoding.UTF8.GetBytes(body)), "application/x-ndjson")
    };

    private static string Record(string response) =>
        JsonSerializer.Serialize(new { model = "qwen3:4b", response, done = false });

    private static string Final(string reason = "stop") =>
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = "", done = true, done_reason = reason, eval_count = 7 });

    private static void AssertSseOrder(string body, params string[] events)
    {
        var cursor = -1;
        foreach (var item in events)
        {
            var next = body.IndexOf($"event: {item}", cursor + 1, StringComparison.Ordinal);
            Assert.True(next > cursor, $"Expected event '{item}' after offset {cursor}. Body: {body}");
            cursor = next;
        }
    }

    private static string ReadDeltaText(string body)
    {
        var lines = body.Split('\n');
        var text = new StringBuilder();
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index] != "event: delta" || !lines[index + 1].StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var json = JsonDocument.Parse(lines[index + 1][6..]);
            text.Append(json.RootElement.GetProperty("text").GetString());
        }

        return text.ToString();
    }
}
