using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>QA-002: behaviour with authentication or chat history switched off, and the shipped defaults.</summary>
[Collection("Qa002")]
public sealed class Qa002ConfigurationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task AuthenticationOff_AuthRoutesDoNotExist()
    {
        var host = QaHost.Create(factory, authentication: false);

        var csrf = await host.Get("/api/auth/csrf");
        var login = await host.Send(HttpMethod.Post, "/api/auth/login", json: new { username = "alice", password = "x" });
        var me = await host.Get("/api/auth/me");

        Qa.Verify("QA2-O-01-auth-off-routes", csrf.Code, csrf.ErrorCode, host.Ollama.Count, ok: login.Code == 404 && me.Code == 404);
    }

    [Fact]
    public async Task AuthenticationOff_ChatIsOpen_ButAdminStillNeedsASignIn()
    {
        var host = QaHost.Create(factory, authentication: false);

        var chat = await host.Chat(null);
        var (head, events) = await host.Stream(null);
        var admin = await host.Get("/api/__test/admin");

        Qa.Verify("QA2-O-02-auth-off-chat-open", chat.Code, chat.ErrorCode, host.Ollama.Count, ok: head.Code == 200 && events.Last().Name == "completed");
        Qa.Verify("QA2-O-03-auth-off-admin", admin.Code, admin.ErrorCode);
    }

    [Fact]
    public void HistoryWithoutAuthentication_DoesNotStart()
    {
        var host = QaHost.Create(factory, history: true, authentication: false);

        var failure = Record.Exception(() => host.Factory.CreateClient());

        Qa.Verify("QA2-O-04-history-needs-auth", ok: failure is not null && (failure.ToString().Contains("ChatHistory:Enabled needs Authentication:Enabled")), detail: failure?.GetType().Name ?? "started");
    }

    [Fact]
    public async Task HistoryOff_RoutesAreNotFound_BeforeAuthentication()
    {
        var host = QaHost.Create(factory);
        var session = await host.Login(host.AddUser("alice"));

        var anonymous = await host.Get("/api/conversations");
        var user = await host.Get("/api/conversations", session.Pair);
        var one = await host.Get($"/api/conversations/{Guid.NewGuid()}/messages", session.Pair);
        Qa.Verify("QA2-O-05-history-off-404", anonymous.Code, anonymous.ErrorCode, host.Ollama.Count, ok: user.Code == 404 && one.Code == 404);

        var chat = await host.Chat(session.Pair, "hello", Guid.NewGuid().ToString());
        var stream = await host.Chat(session.Pair, "hello", Guid.NewGuid().ToString(), stream: true);
        Qa.Verify("QA2-O-06-history-off-chat-conv", chat.Code, chat.ErrorCode, host.Ollama.Count, ok: stream.Code == 404 && stream.ErrorCode == "CONVERSATION_NOT_FOUND");
    }

    [Fact]
    public void ShippedDefault_IsAuthenticationOff()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Qa.RepoRoot, "BC-LocalAI", "appsettings.json")));
        var enabled = settings.RootElement.GetProperty("Authentication").GetProperty("Enabled").GetBoolean();

        Qa.Observe("QA2-O-07-production-default-open", detail: $"BC-LocalAI/appsettings.json Authentication:Enabled = {enabled}: chat endpoints are open until it is turned on");
    }
}
