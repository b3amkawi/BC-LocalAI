using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

/// <summary>/api/chat wired to OllamaAIService with a fake HTTP handler (no real Ollama).</summary>
public sealed class OllamaChatApiTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<HttpResponseMessage> PostAsync(FakeHandler handler, string json, string env = "Development")
    {
        var client = factory.CreateClientWithOllama(handler, env);
        return await client.PostAsync("/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ApiErrorResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(code, body!.Error.Code);
        Assert.Equal(message, body.Error.Message);

        // No stack traces or internals.
        Check.NotLeaked(raw, "   at ");
        Check.NotLeaked(raw, "Exception");
        Check.NotLeaked(raw, "api/generate");
        Check.NotLeaked(raw, "11434");
    }

    [Fact]
    public async Task ValidMessage_Returns200_WithPublicContract_AndFlatAnswer()
    {
        var handler = FakeHandler.OllamaOk("{\"answer\":\"2\"}");

        var response = await PostAsync(handler, """{"message":"หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"response":"2","model":"qwen3:4b"}""", raw);
        Assert.Contains("หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ", Assert.Single(handler.RequestBodies));
    }

    [Theory]
    [InlineData("""{"message":""}""")]
    [InlineData("""{"message":"  "}""")]
    [InlineData("{}")]
    [InlineData("{bad")]
    public async Task InvalidMessage_Returns400_AndNeverCallsOllama(string json)
    {
        var handler = FakeHandler.OllamaOk();

        var response = await PostAsync(handler, json);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_MESSAGE", "Message is required");
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task OllamaUnavailable_Returns503(string env)
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused (secret detail)"));

        var response = await PostAsync(handler, """{"message":"hi"}""", env);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE", "AI service is unavailable");
        Check.NotLeaked(await response.Content.ReadAsStringAsync(), "secret detail");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task OllamaTimeout_Returns504(string env)
    {
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));

        var response = await PostAsync(handler, """{"message":"hi"}""", env);

        await AssertErrorAsync(response, HttpStatusCode.GatewayTimeout, "AI_TIMEOUT", "AI service timed out");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task OllamaHttpError_Returns502_ProviderError(string env)
    {
        var handler = FakeHandler.Returning(
            FakeHandler.Json("{\"error\":\"boom internal\"}", HttpStatusCode.InternalServerError));

        var response = await PostAsync(handler, """{"message":"hi"}""", env);

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "AI_PROVIDER_ERROR", "AI service returned an error");
        Check.NotLeaked(await response.Content.ReadAsStringAsync(), "boom internal");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"answer\\\":\\\"2\\\"}\",\"done\":false}")]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"answer\\\":\\\"2\\\"}\",\"done\":true,\"done_reason\":\"length\"}")]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"plain text\",\"done\":true,\"done_reason\":\"stop\"}")]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"answer\\\":\\\"\\\"}\",\"done\":true,\"done_reason\":\"stop\"}")]
    public async Task InvalidOrIncompleteProviderResponse_Returns502_InvalidResponse(string providerBody)
    {
        var handler = FakeHandler.Returning(FakeHandler.Json(providerBody));

        var response = await PostAsync(handler, """{"message":"hi"}""");

        await AssertErrorAsync(
            response,
            HttpStatusCode.BadGateway,
            "AI_INVALID_RESPONSE",
            "AI service returned an invalid response");
    }

    [Fact]
    public async Task LoneSurrogateInMessage_Returns400Contract_NotInternalError()
    {
        var handler = FakeHandler.OllamaOk();

        // "\\ud800" puts the JSON escape for a lone surrogate on the wire (a plain "\ud800" in C# would be
        // silently replaced by U+FFFD when encoded as UTF-8, which is a valid message).
        var response = await PostAsync(handler, "{\"message\":\"\\ud800 hi\"}");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_MESSAGE", "Message is required");
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task EmojiMessage_ReachesOllamaIntact_AndReturns200()
    {
        var handler = FakeHandler.OllamaOk("{\"answer\":\"ok 😀\"}");

        var response = await PostAsync(handler, "{\"message\":\"😀 สวัสดี\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("ok 😀", body!.Response);
        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("😀 สวัสดี", sent.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Production_DefaultsToOllama_NotMock()
    {
        // No IAIService override: the app must use OllamaAIService (no silent Mock fallback).
        var handler = FakeHandler.OllamaOk("{\"answer\":\"from ollama\"}");

        var response = await PostAsync(handler, """{"message":"hi"}""", "Production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("from ollama", body!.Response);
        Assert.NotEqual("mock", body.Model);
    }

    [Fact]
    public void ShippedConfiguration_IsLoopbackQwen3_4b_180s()
    {
        // Read the file itself so a machine-level Ollama__* variable cannot change the result.
        var environment = factory.WithWebHostBuilder(builder => builder.PinOllama())
            .Services.GetRequiredService<IWebHostEnvironment>();
        var json = File.ReadAllText(Path.Combine(environment.ContentRootPath, "appsettings.json"));
        using var document = JsonDocument.Parse(json);
        var ollama = document.RootElement.GetProperty("Ollama");

        Assert.Equal("http://127.0.0.1:11434", ollama.GetProperty("BaseUrl").GetString());
        Assert.Equal("qwen3:4b", ollama.GetProperty("Model").GetString());
        Assert.Equal(180, ollama.GetProperty("TimeoutSeconds").GetInt32());
    }

    [Fact]
    public void Configuration_CanBeOverriddenByEnvironmentStyleSettings()
    {
        var customized = factory.WithWebHostBuilder(builder =>
            builder.PinOllama("http://127.0.0.1:12345", "qwen3:8b", "30"));

        var options = customized.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;

        Assert.Equal("http://127.0.0.1:12345", options.BaseUrl);
        Assert.Equal("qwen3:8b", options.Model);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("https://ollama.internal:11434")]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("https://ollama.example.com")]
    public void NonLoopbackBaseUrl_StopsApplicationStartup_WithClearMessage(string baseUrl)
    {
        var broken = factory.WithWebHostBuilder(builder => builder.PinOllama(baseUrl));

        var exception = Assert.Throws<OptionsValidationException>(() => broken.CreateClient());

        Assert.Contains("loopback", exception.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://[::1]:11434")]
    public void LoopbackBaseUrl_StartsApplication(string baseUrl)
    {
        var app = factory.WithWebHostBuilder(builder => builder.PinOllama(baseUrl));

        using var client = app.CreateClient();
        var options = app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;

        Assert.Equal(baseUrl, options.BaseUrl);
    }

    [Theory]
    [InlineData("not a url", "qwen3:4b", "180")]
    [InlineData("ftp://127.0.0.1:11434", "qwen3:4b", "180")]
    [InlineData("https://ollama.internal:11434", "qwen3:4b", "180")]
    [InlineData("http://192.168.1.10:11434", "qwen3:4b", "180")]
    [InlineData("https://ollama.example.com", "qwen3:4b", "180")]
    [InlineData("http://0.0.0.0:11434", "qwen3:4b", "180")]
    [InlineData("http://127.0.0.1:11434", "", "180")]
    [InlineData("http://127.0.0.1:11434", "qwen3:4b", "0")]
    [InlineData("http://127.0.0.1:11434", "qwen3:4b", "-5")]
    [InlineData("http://127.0.0.1:11434", "qwen3:4b", "601")]
    public void InvalidConfiguration_FailsAtStartup(string baseUrl, string model, string timeoutSeconds)
    {
        var broken = factory.WithWebHostBuilder(builder => builder.PinOllama(baseUrl, model, timeoutSeconds));

        Assert.Throws<OptionsValidationException>(() => broken.CreateClient());
    }
}
