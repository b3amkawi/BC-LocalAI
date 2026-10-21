using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Services.ChatAdmission;
using BC_LocalAI.Tests.ChatHistory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.ChatAdmission;

/// <summary>BE-008 together with BE-007: a refused or abandoned turn must leave no half-saved conversation behind.</summary>
public sealed class ChatAdmissionHistoryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Alice = "00000000-0000-4000-8000-0000000a11ce";

    private static ChatHistoryApiTests.Host Create(WebApplicationFactory<Program> factory, int queue, int wait = 30) =>
        ChatHistoryApiTests.Create(factory, settings: new Dictionary<string, string?>
        {
            ["ChatQueue:MaxConcurrentRequests"] = "1",
            ["ChatQueue:MaxQueueLength"] = queue.ToString(),
            ["ChatQueue:MaxQueueWaitSeconds"] = wait.ToString(),
            ["ChatQueue:MaxPendingPerUser"] = "4"
        });

    private static async Task<Guid> NewConversation(HttpClient client)
    {
        var created = await client.PostAsync("/api/conversations",
            new StringContent("""{"title":"queue"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<List<(string Role, string Status)>> Messages(HttpClient client, Guid id)
    {
        var body = JsonDocument.Parse(await client.GetStringAsync($"/api/conversations/{id}/messages")).RootElement;
        return body.GetProperty("items").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("status").GetString()!)).ToList();
    }

    private static JsonContent Ask(Guid id) => JsonContent.Create(new { message = "synthetic question", conversationId = id });

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/chat/stream")]
    public async Task RefusedAtAdmission_SavesNothing_AndNeverCallsTheModel(string path)
    {
        var host = Create(factory, queue: 0);
        var alice = await ChatHistoryApiTests.SignedIn(host, Alice);
        var id = await NewConversation(alice);
        using var blocker = host.Security.Factory.Services.GetRequiredService<ChatAdmissionQueue>().Admit("someone-else");

        var refused = await alice.PostAsync(path, Ask(id));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("AI_BUSY", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await Messages(alice, id)); // the question was not saved either
        Assert.Empty(host.Ai.Prompts);
    }

    [Fact]
    public async Task ChatWaitingTooLong_MarksTheTurnFailed_AndTheNextQuestionWorks()
    {
        var host = Create(factory, queue: 2, wait: 1);
        var alice = await ChatHistoryApiTests.SignedIn(host, Alice);
        var id = await NewConversation(alice);
        var gate = host.Security.Factory.Services.GetRequiredService<ChatAdmissionQueue>();
        var blocker = gate.Admit("someone-else");

        var refused = await alice.PostAsync("/api/chat", Ask(id));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("AI_BUSY", await refused.Content.ReadAsStringAsync());
        Assert.Empty(host.Ai.Prompts);
        var messages = await Messages(alice, id);
        Assert.Equal([("user", "completed"), ("assistant", "failed")], messages); // nothing stuck as pending/streaming
        Assert.Equal((1, 0), gate.Snapshot());

        blocker.Dispose();
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/chat", Ask(id))).StatusCode);
        Assert.Equal((0, 0), gate.Snapshot());
    }

    [Fact]
    public async Task StreamWaitingTooLong_EndsWithAiBusy_AndTheTurnIsFailed()
    {
        var host = Create(factory, queue: 2, wait: 1);
        var alice = await ChatHistoryApiTests.SignedIn(host, Alice);
        var id = await NewConversation(alice);
        var gate = host.Security.Factory.Services.GetRequiredService<ChatAdmissionQueue>();
        using var blocker = gate.Admit("someone-else");

        var response = await alice.PostAsync("/api/chat/stream", Ask(id));
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"code\":\"AI_BUSY\"", sse);
        Assert.DoesNotContain("event: completed", sse);
        Assert.Empty(host.Ai.Prompts);
        Assert.Equal([("user", "completed"), ("assistant", "failed")], await Messages(alice, id));
    }
}
