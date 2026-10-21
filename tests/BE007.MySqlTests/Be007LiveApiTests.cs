using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BE007.MySqlTests;

/// <summary>
/// The whole BE-007 path on the real fixture: BE-005 login (reader + attempt writer), BE-006 session, the MySQL history repository
/// with its own runtime account. Only the AI is replaced (no Ollama needed): it records the prompt it receives.
/// </summary>
public sealed class Be007LiveApiTests : IAsyncLifetime
{
    private sealed class RecordingAi : IAIService
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GenerateResponseAsync(string message, CancellationToken cancellationToken = default)
        {
            Prompts.Add(message);
            return Task.FromResult(new ChatResponse($"คำตอบที่ {Prompts.Count}", "qwen3:4b"));
        }
    }

    public Task InitializeAsync() => Be007Fixture.ClearHistory();

    public Task DisposeAsync() => Be007Fixture.ClearHistory();

    private static WebApplicationFactory<Program> App(RecordingAi ai) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:AllowLocalDevelopmentDatabase"] = "true",
                ["Authentication:ReaderConnectionString"] = Be007Fixture.Reader, ["Authentication:WriterConnectionString"] = Be007Fixture.Writer,
                ["Authentication:KeyDirectory"] = Be007Fixture.Directory,
                ["ChatHistory:Enabled"] = "true", ["ChatHistory:ConnectionString"] = Be007Fixture.History
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAIService>();
                services.AddSingleton<IAIService>(ai);
            });
        });

    private static async Task<HttpClient> Login(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var csrf = JsonDocument.Parse(await (await client.GetAsync("/api/auth/csrf")).Content.ReadAsStringAsync());
        var account = Be007Fixture.Active;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = account.Username, password = account.Password })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task SignedInUser_ChatsInAConversation_WithContext_AndItIsStillThereAfterARestart()
    {
        var ai = new RecordingAi();
        Guid id;
        await using (var app = App(ai))
        {
            using var client = await Login(app);
            var created = await client.PostAsJsonAsync("/api/conversations", new { title = "ทดสอบจริง" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await Json(created)).GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/chat", new { message = "คำถามแรก", conversationId = id })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/chat", new { message = "คำถามสอง", conversationId = id })).StatusCode);
        }

        Assert.Equal("คำถามแรก", ai.Prompts[0]);
        Assert.Contains("User: คำถามแรก", ai.Prompts[1]);
        Assert.Contains("Assistant: คำตอบที่ 1", ai.Prompts[1]);

        // A new application host (as after a restart): sessions are gone, the history is not.
        await using var restarted = App(new RecordingAi());
        using var again = await Login(restarted);
        var messages = (await Json(await again.GetAsync($"/api/conversations/{id}/messages"))).GetProperty("items");
        Assert.Equal(["คำถามแรก", "คำตอบที่ 1", "คำถามสอง", "คำตอบที่ 2"], messages.EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        Assert.All(messages.EnumerateArray(), m => Assert.Equal("completed", m.GetProperty("status").GetString()));
        Assert.Equal(id, (await Json(await again.GetAsync("/api/conversations"))).GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnotherUsersConversationInTheDatabase_Is404_AndUnchanged()
    {
        var foreign = Guid.NewGuid();
        await Be007Fixture.AdminExecute(
            "INSERT INTO Conversations (Id, UserId, Title, CreatedAt, UpdatedAt) VALUES (@id, @user, 'foreign', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
            ("@id", foreign.ToByteArray(bigEndian: true)), ("@user", "00000000-0000-4000-8000-0000000f0e19"));
        await using var app = App(new RecordingAi());
        using var client = await Login(app);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/conversations/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/conversations/{foreign}", new { title = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/conversations/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/chat", new { message = "x", conversationId = foreign })).StatusCode);
        Assert.Empty((await Json(await client.GetAsync("/api/conversations"))).GetProperty("items").EnumerateArray());
        Assert.Equal("foreign", await Be007Fixture.AdminScalar("SELECT Title FROM Conversations WHERE Id = @id", ("@id", foreign.ToByteArray(bigEndian: true))));
        Assert.Equal(0L, await Be007Fixture.AdminScalar("SELECT COUNT(*) FROM Messages"));
    }
}
