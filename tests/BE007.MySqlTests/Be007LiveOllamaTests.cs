using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BE007.MySqlTests;

/// <summary>Runs only with BE007_LIVE_OLLAMA=1 and a local Ollama serving qwen3:4b; otherwise skipped (and says so).</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BE007_LIVE_OLLAMA") != "1")
            Skip = "Set BE007_LIVE_OLLAMA=1 with Ollama (qwen3:4b) on 127.0.0.1:11434 to run the real-model check.";
    }
}

/// <summary>
/// Real model, real database, real login: the multi-turn template must let qwen3:4b use an earlier turn, and the answers must
/// still come back in the AI-002 JSON answer format (otherwise the chat would answer 502 AI_INVALID_RESPONSE).
/// </summary>
public sealed class Be007LiveOllamaTests : IAsyncLifetime
{
    public Task InitializeAsync() => Be007Fixture.ClearHistory();

    public Task DisposeAsync() => Be007Fixture.ClearHistory();

    [OllamaFact]
    public async Task TheModelUsesTheEarlierTurn_InChatAndInStream()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
                ["Authentication:ReaderConnectionString"] = Be007Fixture.Reader, ["Authentication:WriterConnectionString"] = Be007Fixture.Writer,
                ["Authentication:KeyDirectory"] = Be007Fixture.Directory,
                ["ChatHistory:Enabled"] = "true", ["ChatHistory:ConnectionString"] = Be007Fixture.History,
                ["Ollama:BaseUrl"] = "http://127.0.0.1:11434", ["Ollama:Model"] = "qwen3:4b"
            }));
        });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using (var csrf = JsonDocument.Parse(await (await client.GetAsync("/api/auth/csrf")).Content.ReadAsStringAsync()))
        {
            var account = Be007Fixture.Active;
            using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = account.Username, password = account.Password })
            };
            login.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        }

        var id = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/conversations", new { title = "live" })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var first = await client.PostAsJsonAsync("/api/chat", new { message = "ฉันชื่อสมชาย และฉันชอบสีเขียว ช่วยจำไว้", conversationId = id });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/chat", new { message = "ฉันชื่ออะไร ตอบแค่ชื่อ", conversationId = id });
        var secondText = await second.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("สมชาย", secondText);

        var stream = await client.PostAsJsonAsync("/api/chat/stream", new { message = "ฉันชอบสีอะไร ตอบสั้นๆ", conversationId = id });
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("event: completed", sse);
        // Deltas may split a Thai word: join them as the UI does before looking for it.
        var streamed = string.Concat(sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.StartsWith("event: delta", StringComparison.Ordinal))
            .Select(block => JsonDocument.Parse(block.Split('\n')[1]["data: ".Length..]).RootElement.GetProperty("text").GetString()));
        Assert.Contains("เขียว", streamed);

        var messages = JsonDocument.Parse(await (await client.GetAsync($"/api/conversations/{id}/messages")).Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(6, messages.Length);
        Assert.All(messages, m => Assert.Equal("completed", m.GetProperty("status").GetString()));
    }
}
