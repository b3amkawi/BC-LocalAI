using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services;
using BC_LocalAI.Services.ChatHistory;
using BC_LocalAI.Tests.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>
/// BE-007 through the real pipeline with authentication (BE-006 sessions) and ChatHistory on; storage is the in-memory reference
/// repository (the MySQL repository passes the same contract in tests/BE007.MySqlTests).
/// </summary>
public sealed class ChatHistoryApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Alice = "00000000-0000-4000-8000-0000000a11ce";
    private const string Bob = "00000000-0000-4000-8000-000000000b0b";

    /// <summary>AI stand-in: records every prompt; answers or fails as the test says.</summary>
    internal sealed class ScriptedAi : IAIService, IStreamingAIService
    {
        public List<string> Prompts { get; } = [];
        public Func<string, string> Answer { get; set; } = prompt => "answer " + (prompt.Length % 97);
        public AiServiceException? Fail { get; set; }
        public string[] Pieces { get; set; } = ["คำ", "ตอบ", " ครบ"];
        public Func<int, Task> AfterPiece { get; set; } = _ => Task.CompletedTask;
        public int FailAfterPieces { get; set; } = -1;

        public Task<ChatResponse> GenerateResponseAsync(string message, CancellationToken cancellationToken = default)
        {
            Prompts.Add(message);
            if (Fail is not null) throw Fail;
            return Task.FromResult(new ChatResponse(Answer(message), "qwen3:4b"));
        }

        public async Task<AiStreamCompletion> StreamResponseAsync(string message, Func<string, CancellationToken, Task> onDelta,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(message);
            for (var i = 0; i < Pieces.Length; i++)
            {
                if (i == FailAfterPieces) throw Fail ?? new AiServiceException("AI_UNAVAILABLE", "AI service is unavailable", HttpStatusCode.ServiceUnavailable);
                await onDelta(Pieces[i], cancellationToken);
                await AfterPiece(i);
            }

            return new AiStreamCompletion("stop", Pieces.Length);
        }
    }

    internal sealed record Host(SecurityHost Security, InMemoryChatHistoryRepository Repo, ScriptedAi Ai)
    {
        public ManualClock Clock => Security.Clock;
    }

    internal static Host Create(WebApplicationFactory<Program> factory, IChatHistoryRepository? repository = null,
        IDictionary<string, string?>? settings = null)
    {
        var security = SecurityHost.Create(factory);
        var repo = new InMemoryChatHistoryRepository(() => security.Clock.Now);
        var ai = new ScriptedAi();
        var configured = new Dictionary<string, string?>
        {
            ["ChatHistory:Enabled"] = "true",
            ["ChatHistory:ConnectionString"] = "Server=localhost;Database=synthetic;User ID=bc_history_rw;Password=placeholder-history;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4" // ci-allow-secret: fake placeholder history configuration, no database is contacted.
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) configured[key] = value;
        security = security with
        {
            Factory = security.Factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configured));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IChatHistoryRepository>();
                    services.AddSingleton(repository ?? repo);
                    services.AddSingleton<IAIService>(ai);
                    services.AddSingleton<IStreamingAIService>(ai);
                });
            })
        };
        security.Users.Put(Alice);
        security.Users.Put(Bob);
        return new Host(security, repo, ai);
    }

    internal static async Task<HttpClient> SignedIn(Host host, string userId)
    {
        var client = host.Security.Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/__test/signin/{userId}", null)).StatusCode);
        return client;
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await Body(response)).GetProperty("error").GetProperty("code").GetString()!;

    private static async Task<Guid> NewConversation(HttpClient client, string title = "งาน")
    {
        var created = await client.PostAsync("/api/conversations", Json(new { title }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Body(created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement[]> Messages(HttpClient client, Guid id) =>
        (await Body(await client.GetAsync($"/api/conversations/{id}/messages"))).GetProperty("items").EnumerateArray().ToArray();

    // ---------------------------------------------------------------- management API

    [Fact]
    public async Task Anonymous_Is401_BeforeAnyStorageWork()
    {
        var host = Create(factory);

        var response = await host.Security.Browser().GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty((await host.Repo.ListConversationsAsync(Alice, new PageRequest())).Items);
    }

    [Fact]
    public async Task CreateListGetRenameDelete_FollowTheContract_AndNeverExposeTheOwner()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);

        var created = await alice.PostAsync("/api/conversations", Json(new { title = "  สรุปงาน  " }));
        var dto = await Body(created);
        var id = dto.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/conversations/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        Assert.Equal("สรุปงาน", dto.GetProperty("title").GetString());
        Assert.Equal(["id", "title", "createdAt", "updatedAt"], dto.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(Alice, await created.Content.ReadAsStringAsync());

        Assert.Equal(id, (await Body(await alice.GetAsync("/api/conversations"))).GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/conversations/{id}")).StatusCode);
        var renamed = await alice.PatchAsync($"/api/conversations/{id}", Json(new { title = "ชื่อใหม่" }));
        Assert.Equal("ชื่อใหม่", (await Body(renamed)).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnotherUser_GetsTheSame404AsForAnIdThatDoesNotExist_AndChangesNothing()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var bob = await SignedIn(host, Bob);
        var id = await NewConversation(alice, "private");
        await alice.PostAsJsonAsync("/api/chat", new { message = "secret question", conversationId = id });
        var missing = Guid.NewGuid();

        async Task<(HttpStatusCode, string)> Probe(Func<Guid, Task<HttpResponseMessage>> call)
        {
            var response = await call(id);
            var unknown = await call(missing);
            Assert.Equal(unknown.StatusCode, response.StatusCode);
            Assert.Equal(await unknown.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var results = new[]
        {
            await Probe(c => bob.GetAsync($"/api/conversations/{c}")),
            await Probe(c => bob.GetAsync($"/api/conversations/{c}/messages")),
            await Probe(c => bob.PatchAsync($"/api/conversations/{c}", Json(new { title = "taken" }))),
            await Probe(c => bob.DeleteAsync($"/api/conversations/{c}")),
            await Probe(c => bob.PostAsync("/api/chat", Json(new { message = "hi", conversationId = c }))),
            await Probe(c => bob.PostAsync("/api/chat/stream", Json(new { message = "hi", conversationId = c })))
        };

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NotFound, r.Item1));
        Assert.All(results, r => Assert.DoesNotContain("secret", r.Item2));
        Assert.Empty((await Body(await bob.GetAsync("/api/conversations"))).GetProperty("items").EnumerateArray());
        Assert.Equal("private", (await host.Repo.GetConversationAsync(Alice, id))!.Title);
        Assert.Equal(2, (await Messages(alice, id)).Length);
    }

    [Theory]
    [InlineData("GET", "/api/conversations/not-a-guid", null, "INVALID_ID")]
    [InlineData("GET", "/api/conversations?limit=0", null, "INVALID_PAGE")]
    [InlineData("GET", "/api/conversations?limit=abc", null, "INVALID_PAGE")]
    [InlineData("GET", "/api/conversations?cursor=%25%25%25", null, "INVALID_PAGE")]
    [InlineData("POST", "/api/conversations", "{broken", "INVALID_REQUEST")]
    [InlineData("POST", "/api/conversations", "{\"title\":\"a\\u0007b\"}", "INVALID_TITLE")]
    public async Task InvalidInput_IsA400WithAStableCode(string method, string path, string? body, string code)
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);

        var response = await alice.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json")
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await Code(response));
    }

    [Fact]
    public async Task List_PagesWithAnOpaqueCursor()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        for (var i = 0; i < 5; i++)
        {
            await NewConversation(alice, $"c{i}");
            host.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await Body(await alice.GetAsync("/api/conversations?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("title").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(["c4", "c3", "c2", "c1", "c0"], seen);
    }

    // ---------------------------------------------------------------- chat inside a conversation

    [Fact]
    public async Task Chat_SavesTheTurn_AndSendsEarlierCompletedTurnsAsContext()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.Answer = prompt => prompt.Contains("มันชื่อ") ? "ชื่อบัดดี้" : "ฉันมีหมาหนึ่งตัว";

        var first = await alice.PostAsync("/api/chat", Json(new { message = "ฉันมีหมา", conversationId = id }));
        var second = await alice.PostAsync("/api/chat", Json(new { message = "มันชื่ออะไร", conversationId = id }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await Body(first);
        Assert.Equal(id, firstBody.GetProperty("conversationId").GetGuid());
        Assert.Equal("ฉันมีหมา", host.Ai.Prompts[0]);                 // nothing earlier: exactly the question, as before BE-007
        Assert.Contains("User: ฉันมีหมา", host.Ai.Prompts[1]);
        Assert.Contains("Assistant: ฉันมีหมาหนึ่งตัว", host.Ai.Prompts[1]);
        Assert.EndsWith(ConversationPrompt.QuestionLabel + Environment.NewLine + "มันชื่ออะไร", host.Ai.Prompts[1]);
        var messages = await Messages(alice, id);
        Assert.Equal(["user", "assistant", "user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.All(messages, m => Assert.Equal("completed", m.GetProperty("status").GetString()));
        Assert.Equal(firstBody.GetProperty("messageId").GetGuid(), messages[1].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task AFailedAnswer_IsStoredAsFailed_NeverCompleted_AndIsNotUsedAsContext()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.Fail = new AiServiceException("AI_TIMEOUT", "AI service timed out", HttpStatusCode.GatewayTimeout);

        var failed = await alice.PostAsync("/api/chat", Json(new { message = "first question", conversationId = id }));
        host.Ai.Fail = null;
        await alice.PostAsync("/api/chat", Json(new { message = "second question", conversationId = id }));

        Assert.Equal(HttpStatusCode.GatewayTimeout, failed.StatusCode);
        var answer = (await Messages(alice, id))[1];
        Assert.Equal(("failed", "AI_TIMEOUT"), (answer.GetProperty("status").GetString(), answer.GetProperty("errorCode").GetString()));
        Assert.Equal("second question", host.Ai.Prompts[^1]);   // the unanswered question is not repeated as context
    }

    [Fact]
    public async Task ContextStaysWithinTheTokenBudget_NewestTurnsFirst()
    {
        var host = Create(factory, settings: new Dictionary<string, string?> { ["ChatHistory:ContextTokenBudget"] = "120" });
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.Answer = _ => new string('ก', 60); // 180 UTF-8 bytes ~ 64 estimated tokens per answer
        for (var i = 1; i <= 4; i++)
        {
            await alice.PostAsync("/api/chat", Json(new { message = $"question {i}", conversationId = id }));
        }

        var prompt = host.Ai.Prompts[^1];

        Assert.Contains("question 3", prompt);
        Assert.DoesNotContain("question 1", prompt);
        Assert.DoesNotContain("question 2", prompt);
    }

    [Fact]
    public async Task AQuestionOverTheTokenLimit_Is400_AndNothingIsSavedOrSentToTheAI()
    {
        var host = Create(factory, settings: new Dictionary<string, string?> { ["ChatHistory:MaxQuestionTokens"] = "100" });
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);

        var tooLong = await alice.PostAsync("/api/chat", Json(new { message = new string('7', 101), conversationId = id }));   // digits: 1 token each
        var fits = await alice.PostAsync("/api/chat", Json(new { message = new string('7', 90), conversationId = id }));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("QUESTION_TOO_LONG", await Code(tooLong));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Single(host.Ai.Prompts);
        Assert.Equal(2, (await Messages(alice, id)).Count());
    }

    [Fact]
    public async Task ADoubleSubmit_WhileAnAnswerIsRunning_Is409_AndTheAIIsNotCalled()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        await host.Repo.AddMessageAsync(Alice, id, MessageRole.User, "running question");
        await host.Repo.AddMessageAsync(Alice, id, MessageRole.Assistant, "");   // pending, just now

        var response = await alice.PostAsync("/api/chat", Json(new { message = "again", conversationId = id }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("HISTORY_CONFLICT", await Code(response));
        Assert.Empty(host.Ai.Prompts);

        // An answer left unfinished long ago (crash) does not block the conversation for ever.
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/chat", Json(new { message = "later", conversationId = id }))).StatusCode);
    }

    [Fact]
    public async Task AnAnswerAbandonedByACrash_ReadsAsInterrupted_AndIsClosedAtTheNextQuestion()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        var crashed = (await host.Repo.AddTurnAsync(Alice, id, "asked before the crash"))!;
        await host.Repo.UpdateMessageAsync(Alice, crashed.Answer.Id, "half an ans", MessageStatus.Streaming);

        // Still inside the AI deadline: it is running, shown as such.
        Assert.Equal("streaming", (await Messages(alice, id))[1].GetProperty("status").GetString());

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var shown = (await Messages(alice, id))[1];
        Assert.Equal(("failed", "INTERRUPTED", "half an ans"),
            (shown.GetProperty("status").GetString(), shown.GetProperty("errorCode").GetString(), shown.GetProperty("content").GetString()));
        Assert.Equal(MessageStatus.Streaming, (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))!.Items[1].Status); // reading never writes

        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/chat", Json(new { message = "next", conversationId = id }))).StatusCode);
        var stored = (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))!.Items[1];
        Assert.Equal((MessageStatus.Failed, "INTERRUPTED", "half an ans"), (stored.Status, stored.ErrorCode, stored.Content));
        Assert.Equal("next", host.Ai.Prompts[^1]);   // the interrupted pair is not context
    }

    [Fact]
    public async Task HistoryStorageDown_Is503_BeforeTheAIIsCalled_WithoutDetails()
    {
        var host = Create(factory, repository: new DownRepository());
        var alice = await SignedIn(host, Alice);

        var list = await alice.GetAsync("/api/conversations");
        var chat = await alice.PostAsync("/api/chat", Json(new { message = "hi", conversationId = Guid.NewGuid() }));
        var stream = await alice.PostAsync("/api/chat/stream", Json(new { message = "hi", conversationId = Guid.NewGuid() }));

        Assert.All([list, chat, stream], r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        Assert.Equal("HISTORY_UNAVAILABLE", await Code(chat));
        Assert.NotEqual("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        Assert.Empty(host.Ai.Prompts);
    }

    [Fact]
    public async Task Chat_SessionEndedWhileWaitingForTheAnswer_Is401_AndTheAnswerIsStoredAsFailed()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        var sessions = host.Security.Factory.Services.GetRequiredService<BC_LocalAI.Security.ISessionStore>();
        host.Ai.Answer = _ =>
        {
            sessions.RevokeAllForUserAsync(Alice, host.Clock.Now).GetAwaiter().GetResult(); // signed out elsewhere meanwhile
            return "an answer nobody may see";
        };

        var response = await alice.PostAsync("/api/chat", Json(new { message = "q", conversationId = id }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("nobody may see", await response.Content.ReadAsStringAsync());
        var stored = (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))!.Items[1];
        Assert.Equal((MessageStatus.Failed, "AUTH_SESSION_ENDED"), (stored.Status, stored.ErrorCode));
        Assert.DoesNotContain("nobody may see", stored.Content);
    }

    [Fact]
    public async Task ConversationDeletedWhileTheAnswerIsProduced_TheAnswerIsNotSavedAnywhere()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.Answer = _ =>
        {
            host.Repo.DeleteConversationAsync(Alice, id).GetAwaiter().GetResult();
            return "late answer";
        };

        var response = await alice.PostAsync("/api/chat", Json(new { message = "q", conversationId = id }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- streaming inside a conversation

    private static List<(string Event, JsonElement Data)> Events(string sse) => sse
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(block => block.Split('\n'))
        .Select(lines => (lines[0]["event: ".Length..], JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone()))
        .ToList();

    [Fact]
    public async Task Stream_StartsWithTheIds_AndCompletedComesOnlyAfterTheFullAnswerIsStored()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        MessageStatus? statusWhileStreaming = null;
        host.Ai.AfterPiece = async i =>
        {
            if (i == 1) statusWhileStreaming = (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))!.Items[1].Status;
        };

        var events = Events(await (await alice.PostAsync("/api/chat/stream", Json(new { message = "q", conversationId = id }))).Content.ReadAsStringAsync());

        Assert.Equal(id, events[0].Data.GetProperty("conversationId").GetGuid());
        Assert.Equal("completed", events[^1].Event);
        Assert.Equal(MessageStatus.Pending, statusWhileStreaming);   // never completed before the answer is complete
        var answer = (await Messages(alice, id))[1];
        Assert.Equal(("completed", "คำตอบ ครบ"), (answer.GetProperty("status").GetString(), answer.GetProperty("content").GetString()));
        Assert.Equal(events[0].Data.GetProperty("messageId").GetGuid(), answer.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Stream_SavesPartialTextAtCheckpoints_AndAnErrorKeepsItAsFailed()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.AfterPiece = _ =>
        {
            host.Clock.Advance(TimeSpan.FromSeconds(3)); // past the 2 s checkpoint interval
            return Task.CompletedTask;
        };
        string? checkpoint = null;
        host.Ai.Pieces = ["ส่วน", "แรก", "ไม่ถึง"];
        host.Ai.FailAfterPieces = 2;
        var originalAfter = host.Ai.AfterPiece;
        host.Ai.AfterPiece = async i =>
        {
            await originalAfter(i);
            checkpoint = (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))?.Items.ElementAtOrDefault(1)?.Content;
        };

        var events = Events(await (await alice.PostAsync("/api/chat/stream", Json(new { message = "q", conversationId = id }))).Content.ReadAsStringAsync());

        Assert.Equal("error", events[^1].Event);
        Assert.Equal("ส่วน", checkpoint is null ? null : checkpoint[..4]);
        var answer = (await Messages(alice, id))[1];
        Assert.Equal(("failed", "ส่วนแรก", "AI_UNAVAILABLE"),
            (answer.GetProperty("status").GetString(), answer.GetProperty("content").GetString(), answer.GetProperty("errorCode").GetString()));
    }

    [Fact]
    public async Task Stream_SessionEndedMidAnswer_IsStoredAsFailed_WithTheSessionCode()
    {
        var host = Create(factory);
        var alice = await SignedIn(host, Alice);
        var id = await NewConversation(alice);
        host.Ai.AfterPiece = async i =>
        {
            if (i == 0) await host.Security.Raw().PostAsync($"/api/__test/revoke-all/{Alice}", null);
        };

        var events = Events(await (await alice.PostAsync("/api/chat/stream", Json(new { message = "q", conversationId = id }))).Content.ReadAsStringAsync());

        Assert.Equal("AUTH_SESSION_ENDED", events[^1].Data.GetProperty("code").GetString());
        var stored = (await host.Repo.ListMessagesAsync(Alice, id, new PageRequest()))!.Items[1];
        Assert.Equal((MessageStatus.Failed, "AUTH_SESSION_ENDED", "คำ"), (stored.Status, stored.ErrorCode, stored.Content));
    }

    [Fact]
    public void HistoryOn_WithAuthenticationOff_RefusesToStart()
    {
        var host = Create(factory, settings: new Dictionary<string, string?> { ["Authentication:Enabled"] = "false" });

        var error = Assert.ThrowsAny<Exception>(() => host.Security.Browser());

        Assert.Contains("needs Authentication:Enabled", error.ToString());
    }

    private sealed class DownRepository : IChatHistoryRepository
    {
        private static ChatHistoryUnavailableException Down() => new();
        public Task<Conversation> CreateConversationAsync(string u, string t, CancellationToken c = default) => throw Down();
        public Task<Page<Conversation>> ListConversationsAsync(string u, PageRequest p, CancellationToken c = default) => throw Down();
        public Task<Conversation?> GetConversationAsync(string u, Guid id, CancellationToken c = default) => throw Down();
        public Task<Conversation?> RenameConversationAsync(string u, Guid id, string t, CancellationToken c = default) => throw Down();
        public Task<bool> DeleteConversationAsync(string u, Guid id, CancellationToken c = default) => throw Down();
        public Task<ChatMessage?> AddMessageAsync(string u, Guid id, MessageRole r, string t, CancellationToken c = default) => throw Down();
        public Task<ChatTurnMessages?> AddTurnAsync(string u, Guid id, string q, CancellationToken c = default) => throw Down();
        public Task<ChatMessage?> UpdateMessageAsync(string u, Guid id, string t, MessageStatus s, string? e = null, CancellationToken c = default) => throw Down();
        public Task<Page<ChatMessage>?> ListMessagesAsync(string u, Guid id, PageRequest p, CancellationToken c = default) => throw Down();
        public Task<IReadOnlyList<ChatMessage>?> GetRecentMessagesAsync(string u, Guid id, int n, CancellationToken c = default) => throw Down();
    }
}
