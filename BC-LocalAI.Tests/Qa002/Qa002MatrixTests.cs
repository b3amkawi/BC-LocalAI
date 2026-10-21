using System.Net;
using BC_LocalAI.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>
/// QA-002 D1: role x endpoint matrix through the real pipeline (login endpoint, cookie, server-side session, policies).
/// Expected values come from tests/security/qa002-cases.json (written before the run); nothing is derived from the Actual result.
/// </summary>
[Collection("Qa002")]
public sealed class Qa002MatrixTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] Roles =
        ["anonymous", "user", "admin", "logged-out", "disabled", "idle-expired", "absolute-expired", "role-changed"];

    /// <summary>The cookie header a caller in this state would send.</summary>
    internal static async Task<string?> CookieFor(QaHost host, string role, string tag)
    {
        if (role == "anonymous") return null;
        var account = host.AddUser($"{tag}-{role}", role == "admin" ? BcRoles.Admin : BcRoles.User);
        var session = await host.Login(account);
        switch (role)
        {
            case "logged-out":
                Assert.Equal(204, (await host.Logout(session)).Code);
                break;
            case "disabled":
                host.Users.Users[account.Id] = host.Users.Users[account.Id] with { IsActive = false };
                break;
            case "idle-expired":
                host.Clock.Advance(TimeSpan.FromMinutes(30));
                break;
            case "absolute-expired":
                for (var i = 0; i < 23; i++)
                {
                    host.Clock.Advance(TimeSpan.FromMinutes(20));
                    Assert.Equal(200, (await host.Get("/api/auth/me", session.Pair)).Code); // active all day
                }

                host.Clock.Advance(TimeSpan.FromMinutes(20)); // 8 hours since sign-in
                break;
            case "role-changed":
                host.Users.Users[account.Id] = host.Users.Users[account.Id] with { Role = BcRoles.Admin };
                break;
        }

        return session.Pair;
    }

    private async Task Run(string key, Func<QaHost, string, string?, string?, string, Task<Resp>> call)
    {
        var host = QaHost.Create(factory, history: true);
        var owner = await host.Login(host.AddUser("owner"));
        var foreign = (await host.Browser(HttpMethod.Post, "/api/conversations", owner.Pair, new { title = "ของคนอื่น" })).Json.GetProperty("id").GetString()!;
        foreach (var role in Roles.Where(r => Qa.Has($"QA2-M-{key}-{r}")))
        {
            var cookie = await CookieFor(host, role, "m-" + key);
            string? mine = null;
            if (role is "user" or "admin")
                mine = (await host.Browser(HttpMethod.Post, "/api/conversations", cookie, new { title = "ของฉัน" })).Json.GetProperty("id").GetString();
            host.Ollama.Reset();
            var response = await call(host, role, cookie, mine, foreign);
            Qa.Verify($"QA2-M-{key}-{role}", response.Code, response.ErrorCode, host.Ollama.Count);
        }
    }

    [Fact]
    public Task Home() => Run("home", (h, _, c, _, _) => h.Get("/", c));

    private Task Run(string key, Func<QaHost, string?, Task<Resp>> call) => Run(key, (h, _, c, _, _) => call(h, c));

    private Task Run(string key, Func<QaHost, string?, string?, string, Task<Resp>> call) => Run(key, (h, _, c, m, f) => call(h, c, m, f));

    [Fact] public Task Csrf() => Run("csrf", (h, c) => h.Get("/api/auth/csrf", c));
    [Fact] public Task Me() => Run("me", (h, c) => h.Get("/api/auth/me", c));

    [Fact]
    public Task Logout() => Run("logout", async (h, c) =>
    {
        var (pair, token) = await h.Csrf(c);
        return await h.Send(HttpMethod.Post, "/api/auth/logout", c is null ? pair : c + "; " + pair, headers: [new("X-CSRF-TOKEN", token)]);
    });

    [Fact] public Task Chat() => Run("chat", (h, c) => h.Chat(c));
    [Fact] public Task Stream() => Run("stream", (h, c) => h.Chat(c, stream: true));
    [Fact] public Task ConversationList() => Run("conv-list", (h, c) => h.Get("/api/conversations", c));
    [Fact] public Task ConversationCreate() => Run("conv-create", (h, c) => h.Browser(HttpMethod.Post, "/api/conversations", c, new { title = "ใหม่" }));
    [Fact] public Task GetOwn() => Run("conv-get-own", (h, c, m, f) => h.Get($"/api/conversations/{m ?? f}", c));
    [Fact] public Task GetOther() => Run("conv-get-other", (h, c, m, f) => h.Get($"/api/conversations/{f}", c));
    [Fact] public Task PatchOwn() => Run("conv-patch-own", (h, c, m, f) => h.Browser(HttpMethod.Patch, $"/api/conversations/{m ?? f}", c, new { title = "เปลี่ยนชื่อ" }));
    [Fact] public Task PatchOther() => Run("conv-patch-other", (h, c, m, f) => h.Browser(HttpMethod.Patch, $"/api/conversations/{f}", c, new { title = "เปลี่ยนชื่อ" }));
    [Fact] public Task DeleteOwn() => Run("conv-delete-own", (h, c, m, f) => h.Browser(HttpMethod.Delete, $"/api/conversations/{m ?? f}", c));
    [Fact] public Task DeleteOther() => Run("conv-delete-other", (h, c, m, f) => h.Browser(HttpMethod.Delete, $"/api/conversations/{f}", c));
    [Fact] public Task MessagesOwn() => Run("conv-msgs-own", (h, c, m, f) => h.Get($"/api/conversations/{m ?? f}/messages", c));
    [Fact] public Task MessagesOther() => Run("conv-msgs-other", (h, c, m, f) => h.Get($"/api/conversations/{f}/messages", c));
    [Fact] public Task ChatIntoOther() => Run("chat-conv-other", (h, c, m, f) => h.Chat(c, "hello", f));
    [Fact] public Task StreamIntoOther() => Run("stream-conv-other", (h, c, m, f) => h.Chat(c, "hello", f, stream: true));
    [Fact] public Task AdminOnly() => Run("admin-only", (h, c) => h.Get("/api/__test/admin", c));
    [Fact] public Task Health() => Run("health", (h, c) => h.Get("/health", c));

    [Fact]
    public async Task AnonymousStream_IsJson401_BeforeAnySse()
    {
        var host = QaHost.Create(factory);
        var response = await host.Chat(null, stream: true);
        Qa.Verify("QA2-M-chat-stream-401-is-json", response.Code, response.ErrorCode, host.Ollama.Count,
            ok: response.Header("Content-Type")!.StartsWith("application/json", StringComparison.Ordinal) && !response.Body.Contains("event:", StringComparison.Ordinal));
    }
}
