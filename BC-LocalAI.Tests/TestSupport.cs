using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests;

/// <summary>Fake HTTP handler so tests never need a running Ollama.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<string> RequestBodies { get; } = [];
    public List<Uri?> RequestUris { get; } = [];
    public List<HttpMethod> RequestMethods { get; } = [];
    public bool TokenCanBeCanceled { get; private set; }

    public static FakeHandler Returning(HttpResponseMessage response) =>
        new((_, _) => Task.FromResult(response));

    public static FakeHandler OllamaOk(string answerJson = "{\"answer\":\"2\"}", string model = "qwen3:4b") =>
        Returning(OllamaResponse(answerJson, model));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        TokenCanBeCanceled = cancellationToken.CanBeCanceled;
        RequestUris.Add(request.RequestUri);
        RequestMethods.Add(request.Method);
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return await respond(request, cancellationToken);
    }

    /// <param name="done">Raw JSON for the "done" field (e.g. true, false, "true").</param>
    public static HttpResponseMessage OllamaResponse(
        string answerJson,
        string model = "qwen3:4b",
        string done = "true",
        string? doneReason = "stop")
    {
        var reason = doneReason is null ? "null" : JsonSerializer.Serialize(doneReason);
        return Json(
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"response":{{JsonSerializer.Serialize(answerJson)}},"done":{{done}},"done_reason":{{reason}}}""");
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class TestApp
{
    /// <summary>
    /// Sets Ollama configuration last, so ambient Ollama__* environment variables on the
    /// machine running the tests can never change the result.
    /// </summary>
    public static IWebHostBuilder PinOllama(
        this IWebHostBuilder builder,
        string baseUrl = "http://127.0.0.1:11434",
        string model = "qwen3:4b",
        string timeoutSeconds = "180") =>
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = baseUrl,
                ["Ollama:Model"] = model,
                ["Ollama:TimeoutSeconds"] = timeoutSeconds
            }));

    /// <summary>App whose Ollama HttpClient is backed by <paramref name="handler"/>.</summary>
    public static HttpClient CreateClientWithOllama(
        this WebApplicationFactory<Program> factory,
        HttpMessageHandler handler,
        string environment = "Development",
        bool pinOllama = true) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (pinOllama)
            {
                builder.PinOllama();
            }
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<IAIService, OllamaAIService>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler));
        }).CreateClient();

    /// <summary>App that answers with <see cref="MockAIService"/> (no Ollama involved).</summary>
    public static HttpClient CreateClientWithMock(
        this WebApplicationFactory<Program> factory,
        string environment = "Development") =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
            builder.ConfigureTestServices(services =>
                services.AddScoped<IAIService, MockAIService>());
        }).CreateClient();
}

internal static class Check
{
    /// <summary>Fails if <paramref name="text"/> contains any of <paramref name="forbidden"/> (ordinal).</summary>
    public static void NotLeaked(string text, params string[] forbidden)
    {
        foreach (var item in forbidden)
        {
            if (text.Contains(item, StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"Response must not contain '{item}' but was: {text}");
            }
        }
    }
}
