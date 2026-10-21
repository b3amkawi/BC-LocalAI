using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BC_LocalAI.Security;
using BC_LocalAI.Tests.ChatHistory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests.Security;

public sealed class FrontendIntegrationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Alice = "00000000-0000-4000-8000-0000000a11ce";
    private const string Bob = "00000000-0000-4000-8000-000000000b0b";
    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()!;

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task RealPagesAreProtectedNoStoreAndUseOnlyRealScripts(string environment)
    {
        var host = ChatHistoryApiTests.Create(factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment)));
        using var anonymous = host.Security.Browser();
        using var root = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/login", root.Headers.Location?.OriginalString);
        Assert.True(root.Headers.CacheControl?.NoStore);
        using var login = await anonymous.GetAsync("/login?returnUrl=https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.CacheControl?.NoStore);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("type=\"password\"", html); Assert.Contains("/js/login.js", html);
        Assert.DoesNotContain("auth-mock", html); Assert.DoesNotContain("auth-preview.js", html);
        Assert.Contains("form-action 'none'", string.Join(';', login.Headers.GetValues("Content-Security-Policy")));
        using var client = await ChatHistoryApiTests.SignedIn(host, Alice);
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"protected-app\" class=\"workspace\" hidden", html);
        Assert.Contains("/js/authenticated-chat.js", html); Assert.DoesNotContain(Alice, html);
        foreach (var path in new[] { "/js/login.js", "/js/frontend-http.js", "/js/history-core.js", "/js/authenticated-chat.js", "/css/authenticated.css" })
        {
            using var asset = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode); Assert.True((await asset.Content.ReadAsByteArrayAsync()).Length > 100);
        }
    }

    [Fact]
    public async Task LoginIsNotExposedWhenAuthenticationIsOff()
    {
        using var client = SecurityHost.Create(factory, enabled: false).Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/login")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "create")]
    [InlineData("PATCH", "rename")]
    [InlineData("DELETE", "delete")]
    [InlineData("POST", "chat")]
    [InlineData("POST", "stream")]
    public async Task MissingCsrfBlocksEveryHistoryMutationBeforePersistenceOrAi(string method, string operation)
    {
        var host = ChatHistoryApiTests.Create(factory);
        using var client = await ChatHistoryApiTests.SignedIn(host, Alice);
        var created = await client.PostAsJsonAsync("/api/conversations", new { title = "unchanged" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var path = operation switch { "create" => "/api/conversations", "chat" => "/api/chat", "stream" => "/api/chat/stream", _ => "/api/conversations/" + id };
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        { Content = operation is "chat" or "stream" ? JsonContent.Create(new { message = "never sent", conversationId = id }) : JsonContent.Create(new { title = "must not write" }) };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal("AUTH_CSRF_INVALID", await Code(response));
        Assert.Empty(host.Ai.Prompts);
        using var unchanged = await client.GetAsync("/api/conversations/" + id);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal("unchanged", (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var messages = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}/messages");
        Assert.Equal(0, messages.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/conversations")]
    [InlineData("/api/auth/me")]
    [InlineData("/API/AUTH/ME/")]
    public async Task IdentityPreconditionRejectsCookieSwitchAndResponseIdentifiesRealActor(string path)
    {
        var host = ChatHistoryApiTests.Create(factory);
        using var client = await ChatHistoryApiTests.SignedIn(host, Alice);
        client.DefaultRequestHeaders.Add(FrontendApiIntegration.ExpectedUserHeader, Alice);
        using var correct = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.Equal(Alice, Assert.Single(correct.Headers.GetValues(FrontendApiIntegration.ActorHeader)));
        Assert.True(correct.Headers.CacheControl?.NoStore);
        await client.PostAsync($"/api/__test/signin/{Bob}", null);
        using var switched = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, switched.StatusCode); Assert.Equal("AUTH_SESSION_CHANGED", await Code(switched));
        Assert.False(switched.Headers.Contains(FrontendApiIntegration.ActorHeader));
    }

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/API/AUTH/LOGOUT/")]
    public async Task StaleLogoutDoesNotLogOutDifferentTabsUser(string path)
    {
        var host = ChatHistoryApiTests.Create(factory);
        using var client = await ChatHistoryApiTests.SignedIn(host, Bob);
        client.DefaultRequestHeaders.Add(FrontendApiIntegration.ExpectedUserHeader, Alice);
        using var response = await client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Remove(FrontendApiIntegration.ExpectedUserHeader);
        using var stillSignedIn = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        Assert.Equal(Bob, (await stillSignedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ForgedExpectedOwnerCannotWriteOrReadAnotherUsersData()
    {
        var host = ChatHistoryApiTests.Create(factory);
        using var alice = await ChatHistoryApiTests.SignedIn(host, Alice);
        using var bob = await ChatHistoryApiTests.SignedIn(host, Bob);
        using var created = await bob.PostAsJsonAsync("/api/conversations", new { title = "B private" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        alice.DefaultRequestHeaders.Add(FrontendApiIntegration.ExpectedUserHeader, Bob);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/conversations/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.PostAsJsonAsync("/api/conversations", new { title = "not written" })).StatusCode);
        alice.DefaultRequestHeaders.Remove(FrontendApiIntegration.ExpectedUserHeader);
        alice.DefaultRequestHeaders.Add(FrontendApiIntegration.ExpectedUserHeader, Alice);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/conversations/" + id)).StatusCode);
        Assert.Equal(0, (await alice.GetFromJsonAsync<JsonElement>("/api/conversations")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task HttpUiApiIsRejectedWithoutRedirectingSubmittedBody()
    {
        var host = ChatHistoryApiTests.Create(factory);
        using var client = host.Security.Raw(); client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Add(FrontendApiIntegration.ExpectedUserHeader, Alice);
        using var response = await client.PostAsJsonAsync("/api/chat/stream", new { message = "never sent" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Null(response.Headers.Location);
        Assert.Equal("AUTH_HTTPS_REQUIRED", await Code(response)); Assert.Empty(host.Ai.Prompts);
    }
}
