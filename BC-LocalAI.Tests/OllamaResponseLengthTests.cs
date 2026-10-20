using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

/// <summary>Creates the real <see cref="OllamaAIService"/> the way Program.cs does (HttpClient without its own timeout).</summary>
internal static class Svc
{
    public static OllamaAIService Create(
        HttpMessageHandler handler,
        OllamaOptions? options = null,
        ILogger<OllamaAIService>? logger = null)
    {
        options ??= new OllamaOptions();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new OllamaAIService(client, Options.Create(options), logger ?? NullLogger<OllamaAIService>.Instance);
    }

    public static async Task<AiServiceException> FailureOf(OllamaAIService service, string message = "hi")
    {
        return await Assert.ThrowsAsync<AiServiceException>(() => service.GenerateResponseAsync(message));
    }
}

/// <summary>
/// BE-003: long answers. The root cause of AI_INVALID_RESPONSE for long answers was the output token limit (num_predict),
/// which cut the answer off (done_reason = length) and left truncated JSON. These tests keep "too long" apart from "malformed".
/// </summary>
public sealed class OllamaResponseLengthTests
{
    private const string Prompt = "hi";

    // ---------------------------------------------------------------- too long is not malformed

    [Theory]
    [InlineData("{\"answer\":\"cut off in the middle of a sentence")] // what Ollama really returned: truncated JSON
    [InlineData("{\"answer\":\"complete\"}")]                         // length is length even if the JSON happens to be complete
    [InlineData("")]
    public async Task DoneReasonLength_IsResponseTooLarge_NotInvalidResponse(string payload)
    {
        var (response, content) = OllamaBodies.Response(OllamaBodies.Bytes(payload, doneReason: "length", evalCount: 1024));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_RESPONSE_TOO_LARGE", failure.Code);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(ResponseTooLargeReasons.OutputTokenLimit, failure.Reason);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task StopWithTruncatedAnswerJson_IsStillMalformed_NotTooLarge()
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"cut", doneReason: "stop"));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_INVALID_RESPONSE", failure.Code);
        Assert.Equal(InvalidResponseReasons.AnswerNotJson, failure.Reason);
    }

    [Fact]
    public async Task NotDone_IsInvalid_EvenWhenReasonSaysLength()
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"x\"}", done: "false", doneReason: "length"));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_INVALID_RESPONSE", failure.Code);
        Assert.Equal(InvalidResponseReasons.NotDone, failure.Reason);
    }

    [Fact]
    public async Task WrongModel_IsInvalid_EvenWhenReasonSaysLength()
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"x\"}", model: "other", doneReason: "length"));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_INVALID_RESPONSE", failure.Code);
        Assert.Equal(InvalidResponseReasons.ModelMismatch, failure.Reason);
    }

    [Theory]
    [InlineData("load", InvalidResponseReasons.UnexpectedDoneReason)]
    [InlineData("unload", InvalidResponseReasons.UnexpectedDoneReason)]
    [InlineData("Stop", InvalidResponseReasons.UnexpectedDoneReason)]   // case-sensitive, as in the AI-002 contract
    [InlineData("LENGTH", InvalidResponseReasons.UnexpectedDoneReason)]
    [InlineData(null, InvalidResponseReasons.UnexpectedDoneReason)]
    public async Task OtherDoneReasons_AreInvalid(string? doneReason, string expectedReason)
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"x\"}", doneReason: doneReason));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_INVALID_RESPONSE", failure.Code);
        Assert.Equal(expectedReason, failure.Reason);
    }

    // ---------------------------------------------------------------- long answers that are valid

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongThaiAnswer_ThatFitsTheLimits_Succeeds_Unchanged(bool relaxedUnicode)
    {
        // ~20,000 Thai characters: about 60 KB as raw UTF-8 and about 120 KB when every character is a \uXXXX escape.
        var answer = string.Concat(Enumerable.Repeat("กรุงเทพมหานคร อมรรัตนโกสินทร์ มหินทรายุธยา ", 450));
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes(
            OllamaBodies.Answer(answer, relaxedUnicode), relaxedUnicode: relaxedUnicode));

        var result = await Svc.Create(FakeHandler.Returning(response)).GenerateResponseAsync(Prompt);

        Assert.True(answer.Length > 15_000);
        Assert.Equal(answer, result.Response);
        Assert.Equal("qwen3:4b", result.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unicode_IsPreservedExactly(bool relaxedUnicode)
    {
        var answer = "ไทย: กรุงเทพฯ น้ำ ก็ ที่ ๑๒๓ | emoji 😀👨‍👩‍👧‍👦🇹🇭 | CJK 漢字かな한글 | RTL עברית العربية | 𠮷 \U0001F9EC | quote \" backslash \\ | tab\tline\nbreak";
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes(
            OllamaBodies.Answer(answer, relaxedUnicode), relaxedUnicode: relaxedUnicode));

        var result = await Svc.Create(FakeHandler.Returning(response)).GenerateResponseAsync(Prompt);

        Assert.Equal(answer, result.Response);
    }

    [Fact]
    public async Task UnicodeEscapesWrittenByHand_AreDecoded()
    {
        // The provider may write Thai either as raw UTF-8 or as ก-style escapes (including surrogate pairs).
        var payload = "{\\\"answer\\\":\\\"\\u0e2a\\u0e27\\u0e31\\u0e2a\\u0e14\\u0e35 \\ud83d\\ude00\\\"}";
        var body = "{\"model\":\"qwen3:4b\",\"response\":\"" + payload + "\",\"done\":true,\"done_reason\":\"stop\"}";
        var (response, _) = OllamaBodies.Response(Encoding.UTF8.GetBytes(body));

        var result = await Svc.Create(FakeHandler.Returning(response)).GenerateResponseAsync(Prompt);

        Assert.Equal("สวัสดี 😀", result.Response);
    }

    // ---------------------------------------------------------------- the response size limit

    private static byte[] PaddedTo(byte[] body, int size)
    {
        // Trailing white space is valid JSON, so the body can be made exactly `size` bytes long.
        var padded = new byte[size];
        Array.Fill(padded, (byte)' ');
        body.CopyTo(padded, 0);
        return padded;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_ExactlyAtTheLimit_Succeeds(bool seekable)
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var body = PaddedTo(OllamaBodies.Bytes("{\"answer\":\"ok\"}"), options.MaxResponseBytes);
        var (response, _) = OllamaBodies.Response(body, seekable: seekable);

        var result = await Svc.Create(FakeHandler.Returning(response), options).GenerateResponseAsync(Prompt);

        Assert.Equal("ok", result.Response);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_OneByteOverTheLimit_IsResponseTooLarge(bool seekable)
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var body = PaddedTo(OllamaBodies.Bytes("{\"answer\":\"ok\"}"), options.MaxResponseBytes + 1);
        var (response, content) = OllamaBodies.Response(body, seekable: seekable);

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response), options));

        Assert.Equal("AI_RESPONSE_TOO_LARGE", failure.Code);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(ResponseTooLargeReasons.BodySizeLimit, failure.Reason);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task DeclaredContentLengthOverTheLimit_IsRejectedWithoutReadingTheBody()
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var stream = new CountingMemoryStream(new byte[options.MaxResponseBytes * 3]);
        var content = new TrackedContent(stream);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response), options));

        Assert.Equal(ResponseTooLargeReasons.BodySizeLimit, failure.Reason);
        Assert.Equal(0, stream.BytesRead); // nothing was read
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task EndlessBody_WithUnknownLength_IsCutOffNearTheLimit_NotReadForever()
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var endless = new EndlessStream();
        var content = new TrackedContent(endless);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response), options));

        Assert.Equal(ResponseTooLargeReasons.BodySizeLimit, failure.Reason);
        // At most the limit plus one read buffer was pulled into memory.
        Assert.InRange(endless.BytesRead, options.MaxResponseBytes, options.MaxResponseBytes + 64 * 1024);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task DefaultLimit_IsOneMebibyte_AndAFewHundredKilobytesAreAccepted()
    {
        Assert.Equal(1_048_576, new OllamaOptions().MaxResponseBytes);
        var answer = new string('ก', 100_000); // ~300 KB raw UTF-8
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes(OllamaBodies.Answer(answer, true), relaxedUnicode: true));

        var result = await Svc.Create(FakeHandler.Returning(response)).GenerateResponseAsync(Prompt);

        Assert.Equal(answer, result.Response);
    }

    // ---------------------------------------------------------------- malformed bodies stay malformed

    [Theory]
    [InlineData("", InvalidResponseReasons.BodyNotJson)]
    [InlineData("not json", InvalidResponseReasons.BodyNotJson)]
    [InlineData("{\"model\":\"qwen3:4b\"", InvalidResponseReasons.BodyNotJson)]
    [InlineData("[]", InvalidResponseReasons.BodyNotJson)]
    [InlineData("\"text\"", InvalidResponseReasons.BodyNotJson)]
    [InlineData("null", InvalidResponseReasons.EmptyBody)]
    public async Task MalformedBodies_AreInvalid_WithAReason(string body, string reason)
    {
        var (response, _) = OllamaBodies.Response(Encoding.UTF8.GetBytes(body));

        var failure = await Svc.FailureOf(Svc.Create(FakeHandler.Returning(response)));

        Assert.Equal("AI_INVALID_RESPONSE", failure.Code);
        Assert.Equal(reason, failure.Reason);
    }

    // ---------------------------------------------------------------- the output token limit sent to Ollama

    [Theory]
    [InlineData(null, 1024)]  // default
    [InlineData(512, 512)]
    [InlineData(4096, 4096)]
    [InlineData(16, 16)]
    public async Task MaxOutputTokens_IsSentToOllamaAsNumPredict(int? configured, int expected)
    {
        var options = new OllamaOptions();
        if (configured is not null)
        {
            options.MaxOutputTokens = configured.Value;
        }

        var handler = FakeHandler.OllamaOk();
        await Svc.Create(handler, options).GenerateResponseAsync(Prompt);

        using var sent = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(expected, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }
}
