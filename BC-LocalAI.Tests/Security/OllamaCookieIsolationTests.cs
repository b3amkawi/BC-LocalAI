using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.Security;

/// <summary>
/// Issue #22 §4.2: the authentication cookie is never sent to Ollama. A signed-in user chats through the real
/// Ollama clients (chat and stream) backed by a recording stub; nothing that reaches Ollama carries the cookie.
/// </summary>
public sealed class OllamaCookieIsolationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record Seen(string Uri, string Headers, string Body);

    private static string StreamBody() => string.Join('\n',
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = "{\"answer\":\"2\"}", done = false }),
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = "", done = true, done_reason = "stop", eval_count = 7 })) + "\n";

    private (SecurityHost Host, List<Seen> Seen) Create()
    {
        var seen = new List<Seen>();
        async Task Record(HttpRequestMessage request, CancellationToken token)
        {
            var headers = string.Join('\n', request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .Select(h => h.Key + ": " + string.Join(",", h.Value)));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            lock (seen) seen.Add(new Seen(request.RequestUri?.ToString() ?? "", headers, body));
        }

        var chat = new FakeHandler(async (request, token) =>
        {
            await Record(request, token);
            return FakeHandler.OllamaResponse("{\"answer\":\"2\"}", "qwen3:4b");
        });
        var stream = new FakeHandler(async (request, token) =>
        {
            await Record(request, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StreamBody(), Encoding.UTF8, "application/x-ndjson")
            };
        });

        var host = SecurityHost.Create(factory);
        host = host with
        {
            Factory = host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                // The real Ollama clients, not the mock answer used by the other security tests.
                services.AddHttpClient<IAIService, OllamaAIService>().ConfigurePrimaryHttpMessageHandler(() => chat);
                services.AddHttpClient<IStreamingAIService, OllamaStreamingService>().ConfigurePrimaryHttpMessageHandler(() => stream);
            }))
        };
        return (host, seen);
    }

    private static async Task<string> SignIn(SecurityHost host, string userId)
    {
        var response = await host.Raw().PostAsync($"/api/__test/signin/{userId}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(BC_LocalAI.Security.BcAuthentication.CookieName + "=", StringComparison.Ordinal));
        return cookie.Split(';', 2)[0]; // name=value
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/chat/stream")]
    public async Task SignedInChat_ReachesOllama_WithoutTheAuthenticationCookie(string path)
    {
        var (host, seen) = Create();
        host.Users.Put("u-1");
        var cookie = await SignIn(host, "u-1");
        var value = cookie[(cookie.IndexOf('=') + 1)..];
        var client = host.Raw();
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"message":"1+1"}""", Encoding.UTF8, "application/json")
        };
        // The browser also sends an unrelated cookie and an Authorization header: none of it may travel on.
        request.Headers.Add("Cookie", cookie + "; theme=dark");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-token-sentinel");
        request.Headers.Add(BC_LocalAI.Security.FrontendApiIntegration.ExpectedUserHeader, "u-1");

        var response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(seen);   // Ollama was really called, once
        Assert.DoesNotContain("Cookie:", call.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization:", call.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BC_LocalAI.Security.FrontendApiIntegration.ExpectedUserHeader, call.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BC_LocalAI.Security.FrontendApiIntegration.ActorHeader, call.Headers, StringComparison.OrdinalIgnoreCase);
        foreach (var leaked in new[] { value, BC_LocalAI.Security.BcAuthentication.CookieName, "theme=dark", "browser-token-sentinel" })
        {
            Assert.DoesNotContain(leaked, call.Uri, StringComparison.Ordinal);
            Assert.DoesNotContain(leaked, call.Headers, StringComparison.Ordinal);
            Assert.DoesNotContain(leaked, call.Body, StringComparison.Ordinal);
        }
    }
}
