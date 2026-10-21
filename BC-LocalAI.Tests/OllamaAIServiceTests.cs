using System.Net;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models.Ollama;
using BC_LocalAI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

public sealed class OllamaAIServiceTests
{
    private static OllamaAIService CreateService(HttpMessageHandler handler, OllamaOptions? options = null)
    {
        options ??= new OllamaOptions();
        var client = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        return new OllamaAIService(client, Options.Create(options), NullLogger<OllamaAIService>.Instance);
    }

    // ---- request building -------------------------------------------------------------------

    [Fact]
    public async Task Request_PostsToGenerateEndpoint_WithContractBody()
    {
        var handler = FakeHandler.OllamaOk();

        await CreateService(handler).GenerateResponseAsync("หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ");

        Assert.Equal(HttpMethod.Post, Assert.Single(handler.RequestMethods));
        Assert.Equal("http://127.0.0.1:11434/api/generate", Assert.Single(handler.RequestUris)!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        var system = root.GetProperty("system").GetString();
        Assert.Equal(OllamaGenerateRequest.SystemPrompt, system);
        Assert.StartsWith(
            "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer.", system);
        Assert.Contains("never repeat or restate the user's message", system);
        Assert.Contains("Examples of the expected behaviour", system);

        var format = root.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.Equal("string", format.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        Assert.Equal("answer", Assert.Single(format.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(format.GetProperty("additionalProperties").GetBoolean());

        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        // BE-003: the output token limit comes from configuration (default 1024, was a fixed 256).
        Assert.Equal(1024, root.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task Request_UsesMessageAsPrompt_AndKeepsThaiTextReadable()
    {
        var handler = FakeHandler.OllamaOk();
        const string message = "หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ";

        await CreateService(handler).GenerateResponseAsync(message);

        var raw = Assert.Single(handler.RequestBodies);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(message, body.RootElement.GetProperty("prompt").GetString());
        Assert.Contains(message, raw); // UTF-8, not \uXXXX escapes
    }

    [Fact]
    public async Task Request_UsesConfiguredModelAndBaseUrl()
    {
        var options = new OllamaOptions { BaseUrl = "http://localhost:9999", Model = "other:1b" };
        var handler = FakeHandler.OllamaOk(model: "other:1b");

        var result = await CreateService(handler, options).GenerateResponseAsync("hi");

        Assert.Equal("http://localhost:9999/api/generate", Assert.Single(handler.RequestUris)!.ToString());
        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("other:1b", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("other:1b", result.Model);
    }

    // ---- success parsing --------------------------------------------------------------------

    [Fact]
    public async Task Success_ReturnsParsedAnswerAndModel_NotNestedJson()
    {
        var result = await CreateService(FakeHandler.OllamaOk("{\"answer\":\"2\"}")).GenerateResponseAsync("1+1");

        Assert.Equal("2", result.Response);
        Assert.Equal("qwen3:4b", result.Model);
        Check.NotLeaked(result.Response, "{");
    }

    [Fact]
    public async Task Success_PreservesThaiAnswer()
    {
        var handler = FakeHandler.OllamaOk("{\"answer\":\"สอง\"}");

        var result = await CreateService(handler).GenerateResponseAsync("1+1");

        Assert.Equal("สอง", result.Response);
    }

    // ---- cancellation -----------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_TokenReachesHttpHandler()
    {
        var handler = FakeHandler.OllamaOk();
        using var cts = new CancellationTokenSource();

        await CreateService(handler).GenerateResponseAsync("hi", cts.Token);

        Assert.True(handler.TokenCanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_ByCaller_StopsPromptly_AsOperationCanceled_NotAiError()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token); // only ends if the caller's token reaches the handler
            return FakeHandler.OllamaResponse("{\"answer\":\"x\"}");
        });
        using var cts = new CancellationTokenSource();
        var task = CreateService(handler).GenerateResponseAsync("hi", cts.Token);
        await cts.CancelAsync();

        // Default HttpClient timeout is 100s; a prompt finish proves the caller token was forwarded.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---- failure mapping --------------------------------------------------------------------

    private static async Task<AiServiceException> Fail(HttpMessageHandler handler)
    {
        var exception = await Assert.ThrowsAsync<AiServiceException>(
            () => CreateService(handler).GenerateResponseAsync("hi"));
        return exception;
    }

    private static void AssertError(AiServiceException exception, string code, HttpStatusCode status)
    {
        Assert.Equal(code, exception.Code);
        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task Unavailable_WhenConnectionFails()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));

        AssertError(await Fail(handler), "AI_UNAVAILABLE", HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Timeout_WhenHttpClientTimesOut()
    {
        var handler = new FakeHandler((_, _) =>
            throw new TaskCanceledException("timeout", new TimeoutException()));

        AssertError(await Fail(handler), "AI_TIMEOUT", HttpStatusCode.GatewayTimeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ProviderError_WhenOllamaReturnsHttpError(HttpStatusCode status)
    {
        var handler = FakeHandler.Returning(FakeHandler.Json("{\"error\":\"model not found\"}", status));

        AssertError(await Fail(handler), "AI_PROVIDER_ERROR", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvalidResponse_WhenDoneIsFalse()
    {
        var handler = FakeHandler.Returning(
            FakeHandler.OllamaResponse("{\"answer\":\"2\"}", done: "false", doneReason: null));

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvalidResponse_WhenDoneIsNotBoolean()
    {
        var handler = FakeHandler.Returning(FakeHandler.OllamaResponse("{\"answer\":\"2\"}", done: "\"true\""));

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("load")]
    [InlineData(null)]
    // "length" is not here any more: BE-003 reports it as AI_RESPONSE_TOO_LARGE (see OllamaResponseLengthTests).
    public async Task InvalidResponse_WhenDoneReasonIsNotStop(string? doneReason)
    {
        var handler = FakeHandler.Returning(
            FakeHandler.OllamaResponse("{\"answer\":\"2\"}", doneReason: doneReason));

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvalidResponse_WhenModelDoesNotMatch()
    {
        var handler = FakeHandler.OllamaOk(model: "qwen3:8b");

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"answer\":")]
    [InlineData("[\"answer\"]")]
    [InlineData("\"2\"")]
    [InlineData("{}")]
    [InlineData("{\"answer\":\"\"}")]
    [InlineData("{\"answer\":\"   \"}")]
    [InlineData("{\"answer\":2}")]
    [InlineData("{\"answer\":null}")]
    [InlineData("{\"answer\":\"2\",\"extra\":\"x\"}")]
    [InlineData("{\"other\":\"2\"}")]
    [InlineData("{\"answer\":\"<think>hmm</think>2\"}")]
    [InlineData("{\"answer\":\"</think>2\"}")]
    [InlineData("{\"answer\":\"<think>2\"}")]
    [InlineData("{\"answer\":\"<THINK>2\"}")]
    [InlineData("")]
    public async Task InvalidResponse_WhenAnswerPayloadIsBad(string answerPayload)
    {
        var handler = FakeHandler.Returning(FakeHandler.OllamaResponse(answerPayload));

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"model\":\"qwen3:4b\"")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task InvalidResponse_WhenOuterBodyIsMalformed(string body)
    {
        var handler = FakeHandler.Returning(FakeHandler.Json(body));

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    public async Task InvalidResponse_WhenOkResponseIsNotJsonContent(string contentType)
    {
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>proxy error page</html>", System.Text.Encoding.UTF8, contentType)
        });

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvalidResponse_WhenOkResponseDeclaresUnsupportedCharset()
    {
        // HttpContent.ReadFromJsonAsync throws NotSupportedException for an unknown charset.
        var content = new StringContent("{\"model\":\"qwen3:4b\"}", System.Text.Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "not-a-real-charset";
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvalidResponse_WhenOkResponseHasEmptyBody()
    {
        var handler = FakeHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });

        AssertError(await Fail(handler), "AI_INVALID_RESPONSE", HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Request_KeepsEmojiAndOtherNonBmpCharactersIntact()
    {
        var handler = FakeHandler.OllamaOk();
        const string message = "😀 สวัสดี 𠮷";

        await CreateService(handler).GenerateResponseAsync(message);

        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(message, sent.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Errors_DoNotLeakProviderDetails()
    {
        var handler = FakeHandler.Returning(
            FakeHandler.Json("{\"error\":\"secret provider detail\"}", HttpStatusCode.InternalServerError));

        var exception = await Fail(handler);

        Check.NotLeaked(exception.Message, "secret provider detail");
    }
}
