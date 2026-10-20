using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests;

/// <summary>
/// BE-003 at the HTTP boundary: POST /api/chat with a fake Ollama (no real Ollama needed).
/// Checks the public error contract for long and oversized answers, the Thai round trip and that configuration reaches Ollama.
/// </summary>
public sealed class OllamaResponseLimitsApiTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static StringContent Json(string message) =>
        new(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");

    private HttpClient Client(HttpMessageHandler handler, string environment = "Development",
        string maxOutputTokens = "1024", string maxResponseBytes = "1048576") =>
        factory.WithWebHostBuilder(builder =>
            builder.PinOllama(maxOutputTokens: maxOutputTokens, maxResponseBytes: maxResponseBytes))
            .CreateClientWithOllama(handler, environment, pinOllama: false);

    // ---------------------------------------------------------------- too large: a typed error

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnswerCutByTheTokenLimit_Returns502_AiResponseTooLarge(string environment)
    {
        var (provider, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"cut off", doneReason: "length", evalCount: 1024));

        var response = await Client(FakeHandler.Returning(provider), environment).PostAsync("/api/chat", Json("เขียนยาว ๆ"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"error":{"code":"AI_RESPONSE_TOO_LARGE","message":"AI response was too large"}}""", raw);
        Check.NotLeaked(raw, "length", "num_predict", "1024", "Exception", "   at ");
    }

    [Fact]
    public async Task OversizedProviderBody_Returns502_AiResponseTooLarge_WithoutLeakingAnything()
    {
        var (provider, content) = OllamaBodies.Response(new byte[200_000], seekable: false);

        var response = await Client(FakeHandler.Returning(provider), maxResponseBytes: "4096")
            .PostAsync("/api/chat", Json("hi"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("AI_RESPONSE_TOO_LARGE", raw);
        Check.NotLeaked(raw, "4096", "200000", "bytes");
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task MalformedBody_StaysAiInvalidResponse_NotTooLarge()
    {
        var (provider, _) = OllamaBodies.Response(Encoding.UTF8.GetBytes("<html>proxy error</html>"));

        var response = await Client(FakeHandler.Returning(provider)).PostAsync("/api/chat", Json("hi"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("AI_INVALID_RESPONSE", await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- long, valid answers

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongThaiAnswer_Returns200_WithTheExactText(bool relaxedUnicode)
    {
        var answer = string.Concat(Enumerable.Repeat("ประเทศไทยมีประวัติศาสตร์ยาวนาน 😀 ", 600));
        var (provider, _) = OllamaBodies.Response(OllamaBodies.Bytes(
            OllamaBodies.Answer(answer, relaxedUnicode), relaxedUnicode: relaxedUnicode, evalCount: 900));

        var response = await Client(FakeHandler.Returning(provider)).PostAsync("/api/chat", Json("เขียนยาว ๆ"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal(answer, body!.Response);
        Assert.Equal("qwen3:4b", body.Model);
        Assert.True(answer.Length > 15_000);
    }

    [Fact]
    public async Task ThaiQuestion_ReachesOllamaIntact_AndThePublicContractIsUnchanged()
    {
        var handler = FakeHandler.OllamaOk("{\"answer\":\"2\"}");

        var response = await Client(handler).PostAsync("/api/chat", Json("หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ"));

        Assert.Equal("""{"response":"2","model":"qwen3:4b"}""", await response.Content.ReadAsStringAsync());
        Assert.Contains("หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ", Assert.Single(handler.RequestBodies)); // raw UTF-8, not \u escapes
    }

    // ---------------------------------------------------------------- configuration reaches Ollama

    [Theory]
    [InlineData("1024")]
    [InlineData("512")]
    [InlineData("4096")]
    public async Task ConfiguredOutputTokenLimit_IsWhatOllamaReceives(string configured)
    {
        var handler = FakeHandler.OllamaOk();

        await Client(handler, maxOutputTokens: configured).PostAsync("/api/chat", Json("hi"));

        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(int.Parse(configured), sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task RequestToOllama_KeepsTheRestOfTheAi002Contract()
    {
        var handler = FakeHandler.OllamaOk();

        await Client(handler).PostAsync("/api/chat", Json("hi"));

        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = sent.RootElement;
        Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
    }

    // ---------------------------------------------------------------- regression of the existing contract

    [Fact]
    public async Task ExistingErrorContract_Unchanged()
    {
        async Task<(HttpStatusCode, string)> Post(HttpMessageHandler handler, string json)
        {
            var response = await Client(handler).PostAsync("/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var (s400, b400) = await Post(FakeHandler.OllamaOk(), """{"message":""}""");
        Assert.Equal(HttpStatusCode.BadRequest, s400);
        Assert.Contains("INVALID_MESSAGE", b400);

        var (s503, b503) = await Post(new FakeHandler((_, _) => throw new HttpRequestException("refused")), """{"message":"hi"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, s503);
        Assert.Contains("AI_UNAVAILABLE", b503);

        var (s504, b504) = await Post(
            new FakeHandler((_, _) => throw new TaskCanceledException("t", new TimeoutException())), """{"message":"hi"}""");
        Assert.Equal(HttpStatusCode.GatewayTimeout, s504);
        Assert.Contains("AI_TIMEOUT", b504);

        var (s502, b502) = await Post(
            FakeHandler.Returning(FakeHandler.Json("{}", HttpStatusCode.InternalServerError)), """{"message":"hi"}""");
        Assert.Equal(HttpStatusCode.BadGateway, s502);
        Assert.Contains("AI_PROVIDER_ERROR", b502);
    }
}
