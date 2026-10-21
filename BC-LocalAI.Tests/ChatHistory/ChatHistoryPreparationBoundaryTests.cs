using System.Net;
using System.Text;
using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>
/// ChatHistory:Enabled is false by default: the history routes do not exist (404 before authentication, so a disabled
/// feature cannot be probed through 401/403), a chat request that names a conversation is refused, and nothing is stored.
/// </summary>
public sealed class ChatHistoryPreparationBoundaryTests
{
    /// <summary>Fails the test if the application touches history storage at all.</summary>
    private sealed class ForbiddenRepository : IChatHistoryRepository
    {
        private static Exception Touched() => new Xunit.Sdk.XunitException("History storage was used while ChatHistory is disabled.");
        public Task<Conversation> CreateConversationAsync(string u, string t, CancellationToken c = default) => throw Touched();
        public Task<Page<Conversation>> ListConversationsAsync(string u, PageRequest p, CancellationToken c = default) => throw Touched();
        public Task<Conversation?> GetConversationAsync(string u, Guid id, CancellationToken c = default) => throw Touched();
        public Task<Conversation?> RenameConversationAsync(string u, Guid id, string t, CancellationToken c = default) => throw Touched();
        public Task<bool> DeleteConversationAsync(string u, Guid id, CancellationToken c = default) => throw Touched();
        public Task<ChatMessage?> AddMessageAsync(string u, Guid id, MessageRole r, string t, CancellationToken c = default) => throw Touched();
        public Task<ChatTurnMessages?> AddTurnAsync(string u, Guid id, string q, CancellationToken c = default) => throw Touched();
        public Task<ChatMessage?> UpdateMessageAsync(string u, Guid id, string t, MessageStatus s, string? e = null, CancellationToken c = default) => throw Touched();
        public Task<Page<ChatMessage>?> ListMessagesAsync(string u, Guid id, PageRequest p, CancellationToken c = default) => throw Touched();
        public Task<IReadOnlyList<ChatMessage>?> GetRecentMessagesAsync(string u, Guid id, int n, CancellationToken c = default) => throw Touched();
    }

    private static WebApplicationFactory<Program> App(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatHistoryRepository>();
                services.AddScoped<IChatHistoryRepository, ForbiddenRepository>();
                services.AddScoped<IAIService, MockAIService>();
            });
        });

    [Theory]
    [InlineData("Development", "GET", "/api/conversations")]
    [InlineData("Production", "GET", "/api/conversations")]
    [InlineData("Development", "POST", "/api/conversations")]
    [InlineData("Development", "GET", "/api/conversations/00000000-0000-0000-0000-000000000001")]
    [InlineData("Development", "PATCH", "/api/conversations/00000000-0000-0000-0000-000000000001")]
    [InlineData("Development", "DELETE", "/api/conversations/00000000-0000-0000-0000-000000000001")]
    [InlineData("Production", "GET", "/api/conversations/00000000-0000-0000-0000-000000000001/messages")]
    public async Task HistoryRoutesDoNotExist_WhileDisabled(string environment, string method, string path)
    {
        await using var factory = App(environment);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("""{"title":"x"}""", Encoding.UTF8, "application/json")
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/chat/stream")]
    public async Task ChatNamingAConversation_IsRefused_WhileDisabled_AndNothingIsStored(string path)
    {
        await using var factory = App("Development");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(path, new StringContent(
            """{"message":"hello","conversationId":"00000000-0000-0000-0000-000000000001"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("CONVERSATION_NOT_FOUND", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChatWithoutAConversation_IsUnchanged_AndNothingIsStored()
    {
        await using var factory = App("Development");
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/chat", new StringContent("""{"message":"hello"}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("conversationId", body);
        Assert.DoesNotContain("messageId", body);
    }
}
