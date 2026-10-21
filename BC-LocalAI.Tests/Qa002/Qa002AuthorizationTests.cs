using System.Text.Json;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Security;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>QA-002 3.4 Authorization, IDOR on conversation ids (URL and body), privilege escalation.</summary>
[Collection("Qa002")]
public sealed class Qa002AuthorizationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record World(QaHost Host, Acct Alice, Acct Bob, Acct Admin, Session A, Session B, Session Root);

    private async Task<World> Create()
    {
        var host = QaHost.Create(factory, history: true);
        var alice = host.AddUser("alice");
        var bob = host.AddUser("bob");
        var admin = host.AddUser("admin1", BcRoles.Admin);
        return new World(host, alice, bob, admin, await host.Login(alice), await host.Login(bob), await host.Login(admin));
    }

    private static async Task<string> NewConversation(QaHost host, Session session, string title)
    {
        var response = await host.Browser(HttpMethod.Post, "/api/conversations", session.Pair, new { title });
        Assert.Equal(201, response.Code);
        return response.Json.GetProperty("id").GetString()!;
    }

    private static async Task<string[]> Titles(QaHost host, Session session, string query = "") =>
        (await host.Get("/api/conversations" + query, session.Pair)).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task List_ShowsOnlyTheCallersConversations()
    {
        var w = await Create();
        await NewConversation(w.Host, w.A, "alice-1");
        await NewConversation(w.Host, w.A, "alice-2");
        await NewConversation(w.Host, w.B, "bob-1");

        var alice = await Titles(w.Host, w.A);
        var bob = await Titles(w.Host, w.B);

        Qa.Verify("QA2-I-01-list-own-only", 200, ok: alice.Order().SequenceEqual(["alice-1", "alice-2"]) && bob.SequenceEqual(["bob-1"]));
    }

    [Fact]
    public async Task OthersConversation_LooksExactlyLikeAMissingOne()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "alice-private");

        var other = await w.Host.Get($"/api/conversations/{id}", w.B.Pair);
        var missing = await w.Host.Get($"/api/conversations/{Guid.NewGuid()}", w.B.Pair);
        var otherMessages = await w.Host.Get($"/api/conversations/{id}/messages", w.B.Pair);
        var missingMessages = await w.Host.Get($"/api/conversations/{Guid.NewGuid()}/messages", w.B.Pair);

        Qa.Verify("QA2-I-02-other-same-as-missing", other.Code, other.ErrorCode, ok: other.Body == missing.Body && otherMessages.Body == missingMessages.Body && other.Code == missing.Code);
    }

    [Fact]
    public async Task OtherUsersAttempts_ChangeNothing()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "alice-private");
        Assert.Equal(200, (await w.Host.Chat(w.A.Pair, "alice question", id)).Code);
        var before = (await w.Host.Get($"/api/conversations/{id}/messages", w.A.Pair)).Body;

        var attempts = new[]
        {
            await w.Host.Browser(HttpMethod.Patch, $"/api/conversations/{id}", w.B.Pair, new { title = "hijacked" }),
            await w.Host.Browser(HttpMethod.Delete, $"/api/conversations/{id}", w.B.Pair),
            await w.Host.Get($"/api/conversations/{id}/messages", w.B.Pair),
            await w.Host.Get($"/api/conversations/{id}", w.B.Pair)
        };
        var after = (await w.Host.Get($"/api/conversations/{id}/messages", w.A.Pair)).Body;
        var still = await w.Host.Get($"/api/conversations/{id}", w.A.Pair);

        Qa.Verify("QA2-I-03-other-unchanged", ok: attempts.All(a => a.Code == 404) && before == after && still.Code == 200 && still.Json.GetProperty("title").GetString() == "alice-private");
    }

    [Fact]
    public async Task ChatIntoSomeoneElsesConversation_IsRefused_AndWritesNothing()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "alice-private");
        var before = (await w.Host.Get($"/api/conversations/{id}/messages", w.A.Pair)).Body;
        w.Host.Ollama.Reset();

        var chat = await w.Host.Chat(w.B.Pair, "intruder question", id);
        var stream = await w.Host.Chat(w.B.Pair, "intruder question", id, stream: true);
        var after = (await w.Host.Get($"/api/conversations/{id}/messages", w.A.Pair)).Body;

        Qa.Verify("QA2-I-04-chat-other-conv-no-write", chat.Code, chat.ErrorCode, w.Host.Ollama.Count,
            ok: stream.Code == 404 && stream.ErrorCode == "CONVERSATION_NOT_FOUND" && before == after && !stream.Body.Contains("event:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_CannotReadOrChangeAUsersConversation()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "alice-private");
        w.Host.Ollama.Reset();

        var results = new[]
        {
            await w.Host.Get($"/api/conversations/{id}", w.Root.Pair),
            await w.Host.Browser(HttpMethod.Patch, $"/api/conversations/{id}", w.Root.Pair, new { title = "admin" }),
            await w.Host.Browser(HttpMethod.Delete, $"/api/conversations/{id}", w.Root.Pair),
            await w.Host.Get($"/api/conversations/{id}/messages", w.Root.Pair),
            await w.Host.Chat(w.Root.Pair, "admin question", id),
            await w.Host.Chat(w.Root.Pair, "admin question", id, stream: true)
        };
        var adminList = await Titles(w.Host, w.Root);

        Qa.Verify("QA2-I-05-admin-no-read", results[0].Code, results[0].ErrorCode, w.Host.Ollama.Count,
            ok: results.All(r => r.Code == 404 && r.ErrorCode == "CONVERSATION_NOT_FOUND") && adminList.Length == 0);
    }

    [Fact]
    public async Task MalformedIds_Are400_NeverAServerError()
    {
        var w = await Create();
        var ids = new[]
        {
            "not-a-guid", "%27%20OR%201%3D1", Guid.NewGuid().ToString("N"), "%7B" + Guid.NewGuid() + "%7D", new string('a', 300), "0", "00000000-0000-0000-0000-00000000000g"
        };
        var codes = new List<string>();
        foreach (var id in ids)
        {
            var response = await w.Host.Get($"/api/conversations/{id}", w.A.Pair);
            codes.Add($"{response.Code}/{response.ErrorCode}");
        }

        var firstBad = codes.FirstOrDefault(c => c != "400/INVALID_ID");
        Qa.Verify("QA2-I-06-invalid-ids", firstBad is null ? 400 : int.Parse(firstBad.Split('/')[0]), firstBad is null ? "INVALID_ID" : firstBad.Split('/')[1], detail: string.Join(" ", codes));
    }

    [Fact]
    public async Task OwnerComesFromTheSession_NotFromTheRequest()
    {
        var w = await Create();

        var create = await w.Host.Browser(HttpMethod.Post, "/api/conversations", w.B.Pair, new { title = "bob-made", userId = w.Alice.Id, ownerId = w.Alice.Id });
        var aliceSees = await Titles(w.Host, w.A);
        var bobSees = await Titles(w.Host, w.B);
        Qa.Verify("QA2-I-07-body-owner-ignored", create.Code, create.ErrorCode, ok: aliceSees.Length == 0 && bobSees.SequenceEqual(["bob-made"]));

        await NewConversation(w.Host, w.A, "alice-secret");
        var spoofed = await w.Host.Get($"/api/conversations?userId={w.Alice.Id}&ownerId={w.Alice.Id}", w.B.Pair,
            [new("X-User-Id", w.Alice.Id), new("X-Forwarded-User", w.Alice.Name), new("X-Original-User", w.Alice.Id)]);
        var titles = spoofed.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToArray();
        Qa.Verify("QA2-I-08-header-query-ignored", spoofed.Code, spoofed.ErrorCode, ok: titles.SequenceEqual(["bob-made"]));
    }

    [Fact]
    public async Task Cursor_OfAnotherUser_GivesNothingOfTheirs()
    {
        var w = await Create();
        for (var i = 0; i < 3; i++) await NewConversation(w.Host, w.A, "alice-" + i);
        await NewConversation(w.Host, w.B, "bob-0");
        var page = (await w.Host.Get("/api/conversations?limit=1", w.A.Pair)).Json;
        var cursor = page.GetProperty("nextCursor").GetString()!;

        var stolen = await w.Host.Get("/api/conversations?limit=10&cursor=" + Uri.EscapeDataString(cursor), w.B.Pair);

        var leaked = stolen.Code == 200 && stolen.Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString()!.StartsWith("alice-", StringComparison.Ordinal));
        Qa.Verify("QA2-I-09-cursor-cross-user", ok: !leaked && stolen.Code is 200 or 400, detail: $"status {stolen.Code}");
    }

    [Fact]
    public async Task DeletedConversation_Is404_ForTheOwnerToo()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "to-delete");
        var bobs = await NewConversation(w.Host, w.B, "bob-keeps");
        Assert.Equal(204, (await w.Host.Browser(HttpMethod.Delete, $"/api/conversations/{id}", w.A.Pair)).Code);

        var gone = await w.Host.Get($"/api/conversations/{id}", w.A.Pair);
        var bobStill = await w.Host.Get($"/api/conversations/{bobs}", w.B.Pair);

        Qa.Verify("QA2-I-10-deleted-404", gone.Code, gone.ErrorCode, ok: bobStill.Code == 200);
    }

    [Fact]
    public async Task Dtos_DoNotExposeOwnerOrInternalFields()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "alice");
        await w.Host.Chat(w.A.Pair, "question", id);
        var bodies = new[]
        {
            (await w.Host.Get("/api/conversations", w.A.Pair)).Body,
            (await w.Host.Get($"/api/conversations/{id}", w.A.Pair)).Body,
            (await w.Host.Get($"/api/conversations/{id}/messages", w.A.Pair)).Body
        };
        string[] forbidden = ["userId", "ownerId", "owner", "nextSequence", "deleted", w.Alice.Id];

        Qa.Verify("QA2-I-11-no-owner-in-dto", ok: !bodies.Any(b => forbidden.Any(f => b.Contains(f, StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public async Task Patch_ChangesOnlyTheTitle()
    {
        var w = await Create();
        var id = await NewConversation(w.Host, w.A, "before");
        var original = (await w.Host.Get($"/api/conversations/{id}", w.A.Pair)).Json;

        var patched = await w.Host.Browser(HttpMethod.Patch, $"/api/conversations/{id}", w.A.Pair,
            new { title = "after", id = Guid.NewGuid(), userId = w.Bob.Id, createdAt = "2000-01-01T00:00:00Z" });

        Qa.Verify("QA2-I-12-patch-mass-assign", patched.Code, patched.ErrorCode,
            ok: patched.Json.GetProperty("title").GetString() == "after" && patched.Json.GetProperty("id").GetString() == id &&
                patched.Json.GetProperty("createdAt").GetString() == original.GetProperty("createdAt").GetString() && (await Titles(w.Host, w.B)).Length == 0);
    }

    [Fact]
    public async Task UserCannotUseAdminEndpoints()
    {
        var w = await Create();

        var response = await w.Host.Get("/api/__test/admin", w.A.Pair);

        Qa.Verify("QA2-I-13-user-admin-endpoint", response.Code, response.ErrorCode, ok: !response.Body.Contains("admin\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForgedRoleCookie_FromAnotherKeyRing_IsNotAccepted()
    {
        var w = await Create();
        var options = w.Host.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BcAuthentication.Scheme);
        // an attacker cannot know the application's keys: a ticket of the same shape protected with other keys must not be accepted
        var forger = new TicketDataFormat(new EphemeralDataProtectionProvider().CreateProtector("attacker"));
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, w.Alice.Id), new Claim(ClaimTypes.Role, BcRoles.Admin), new Claim(BcClaimTypes.SessionId, "forged")],
            BcAuthentication.Scheme);
        var forged = forger.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), BcAuthentication.Scheme));
        Assert.NotNull(options.TicketDataFormat); // the real format exists and is a different key ring

        var response = await w.Host.Get("/api/__test/admin", BcAuthentication.CookieName + "=" + forged);

        Qa.Verify("QA2-I-14-forged-role-cookie", response.Code, response.ErrorCode);
    }

    [Fact]
    public async Task Role_ComesFromTheAccount_NotFromTheLoginBackend()
    {
        QaHost? host = null;
        host = QaHost.Create(factory, services: s =>
        {
            s.RemoveAll<IAuthenticationBackend>();
            s.AddSingleton<IAuthenticationBackend>(new AlwaysAdminBackend(() => host!.Accounts["MALLORY"].Id));
        });
        var mallory = host.AddUser("mallory");

        var login = await host.LoginRaw(mallory.Name, "anything");
        var me = await host.Get("/api/auth/me", login.SessionSetCookie!.Split(';', 2)[0]);
        var roles = me.Json.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray();
        var admin = await host.Get("/api/__test/admin", login.SessionSetCookie!.Split(';', 2)[0]);

        Qa.Verify("QA2-I-15-login-role-from-account", login.Code, login.ErrorCode, ok: roles.SequenceEqual(["User"]) && admin.Code == 403);
    }

    /// <summary>A login backend that claims Admin for the account, to prove the session takes the role from the account store.</summary>
    private sealed class AlwaysAdminBackend(Func<string> id) : IAuthenticationBackend
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuthenticationResult.Succeeded(new(id(), "mallory", "mallory", [BcRoles.Admin])));
    }
}
