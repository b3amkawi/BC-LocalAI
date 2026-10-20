using System.Net;
using System.Text;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services;
using Microsoft.Extensions.Logging;

namespace BC_LocalAI.Tests;

/// <summary>
/// BE-003: structured, safe logging. Every Ollama outcome is one log entry with named fields
/// (searchable), and neither the question nor the answer is ever written to a log.
/// </summary>
public sealed class OllamaLoggingTests
{
    private const string PromptSecret = "PROMPT-SECRET-7f3a91";
    private const string AnswerSecret = "ANSWER-SECRET-c20b44";

    private static (OllamaAIService Service, CapturingLoggerProvider Logs) Create(HttpMessageHandler handler, OllamaOptions? options = null)
    {
        var logs = new CapturingLoggerProvider();
        return (Svc.Create(handler, options, logs.CreateLogger<OllamaAIService>()), logs);
    }

    private static LogEntry Single(CapturingLoggerProvider logs) =>
        Assert.Single(logs.Entries, e => e.EventId.Id >= 4001 && e.EventId.Id <= 4007);

    // ---------------------------------------------------------------- outcomes

    [Fact]
    public async Task Success_IsOneInformationEntry_WithNumbersOnly()
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes(OllamaBodies.Answer(AnswerSecret), evalCount: 42));
        var (service, logs) = Create(FakeHandler.Returning(response));

        await service.GenerateResponseAsync(PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4001, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("qwen3:4b", entry.Fields["Model"]);
        Assert.Equal(42, entry.Fields["EvalTokens"]);
        Assert.Equal(AnswerSecret.Length, entry.Fields["AnswerChars"]);
        Assert.Equal(1024, entry.Fields["MaxOutputTokens"]);
        Assert.True((int)entry.Fields["BodyBytes"]! > 0);
        Assert.True((long)entry.Fields["ElapsedMs"]! >= 0);
    }

    [Fact]
    public async Task OutputTokenLimit_IsOneWarning_WithTheReasonAndTheNumbers()
    {
        var (response, _) = OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"cut", doneReason: "length", evalCount: 1024));
        var (service, logs) = Create(FakeHandler.Returning(response));

        await Svc.FailureOf(service, PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4007, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(ResponseTooLargeReasons.OutputTokenLimit, entry.Fields["Reason"]);
        Assert.Equal(1024, entry.Fields["EvalTokens"]);
        Assert.Equal(1024, entry.Fields["MaxOutputTokens"]);
        Assert.Equal(1_048_576, entry.Fields["MaxResponseBytes"]);
    }

    [Fact]
    public async Task BodySizeLimit_IsOneWarning_WithTheReasonAndTheLimit()
    {
        var options = new OllamaOptions { MaxResponseBytes = 4096 };
        var (response, _) = OllamaBodies.Response(new byte[8192]);
        var (service, logs) = Create(FakeHandler.Returning(response), options);

        await Svc.FailureOf(service, PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4007, entry.EventId.Id);
        Assert.Equal(ResponseTooLargeReasons.BodySizeLimit, entry.Fields["Reason"]);
        Assert.Equal(4096, entry.Fields["MaxResponseBytes"]);
        Assert.True((long)entry.Fields["BodyBytes"]! > 4096);
    }

    [Theory]
    [InlineData("not json", InvalidResponseReasons.BodyNotJson)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{}\",\"done\":false}", InvalidResponseReasons.NotDone)]
    [InlineData("{\"model\":\"other\",\"response\":\"{}\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.ModelMismatch)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{}\",\"done\":true,\"done_reason\":\"load\"}", InvalidResponseReasons.UnexpectedDoneReason)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.EmptyAnswerPayload)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"nope\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.AnswerNotJson)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"[1]\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.AnswerNotObject)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"a\\\":1}\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.AnswerFieldInvalid)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"answer\\\":\\\" \\\"}\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.AnswerEmpty)]
    [InlineData("{\"model\":\"qwen3:4b\",\"response\":\"{\\\"answer\\\":\\\"<think>x</think>\\\"}\",\"done\":true,\"done_reason\":\"stop\"}", InvalidResponseReasons.ThinkMarker)]
    public async Task InvalidResponse_IsOneWarning_WithABoundedReason(string body, string reason)
    {
        var (response, _) = OllamaBodies.Response(Encoding.UTF8.GetBytes(body));
        var (service, logs) = Create(FakeHandler.Returning(response));

        var failure = await Svc.FailureOf(service, PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4006, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(reason, entry.Fields["Reason"]);
        Assert.Equal(reason, failure.Reason);
    }

    [Fact]
    public async Task ProviderHttpError_IsOneWarning_WithTheStatusCode()
    {
        var (response, _) = OllamaBodies.Response(Encoding.UTF8.GetBytes("{\"error\":\"" + AnswerSecret + "\"}"), HttpStatusCode.NotFound);
        var (service, logs) = Create(FakeHandler.Returning(response));

        await Svc.FailureOf(service, PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4005, entry.EventId.Id);
        Assert.Equal(404, entry.Fields["StatusCode"]);
    }

    [Fact]
    public async Task Unavailable_IsOneWarning_WithTheLoopbackAddress_AndTheExceptionForDiagnosis()
    {
        var (service, logs) = Create(new FakeHandler((_, _) => throw new HttpRequestException("connection refused")));

        await Svc.FailureOf(service, PromptSecret);

        var entry = Single(logs);
        Assert.Equal(4004, entry.EventId.Id);
        Assert.Equal("http://127.0.0.1:11434", entry.Fields["BaseUrl"]);
        Assert.IsType<HttpRequestException>(entry.Exception);
    }

    [Fact]
    public async Task Timeout_IsOneWarning_WithTheDeadlineAndTheElapsedTime()
    {
        var hang = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        var (service, logs) = Create(hang, new OllamaOptions { TimeoutSeconds = 1 });

        await Svc.FailureOf(service, PromptSecret).WaitAsync(TimeSpan.FromSeconds(10)); // fails fast if no deadline exists

        var entry = Single(logs);
        Assert.Equal(4002, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, entry.Fields["TimeoutSeconds"]);
        Assert.InRange((long)entry.Fields["ElapsedMs"]!, 800, 5000);
    }

    [Fact]
    public async Task ClientCancellation_IsInformation_NotAWarning_SoItIsNotCountedAsAnAiFailure()
    {
        var hang = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        var (service, logs) = Create(hang);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateResponseAsync(PromptSecret, caller.Token));

        var entry = Single(logs);
        Assert.Equal(4003, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ---------------------------------------------------------------- safety

    [Fact]
    public async Task NoOutcome_EverLogsTheQuestionOrTheAnswer()
    {
        var scenarios = new List<Func<(HttpMessageHandler Handler, OllamaOptions Options)>>
        {
            () => (FakeHandler.Returning(OllamaBodies.Response(OllamaBodies.Bytes(OllamaBodies.Answer(AnswerSecret))).Response), new OllamaOptions()),
            () => (FakeHandler.Returning(OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"" + AnswerSecret, doneReason: "length")).Response), new OllamaOptions()),
            () => (FakeHandler.Returning(OllamaBodies.Response(OllamaBodies.Bytes("{\"answer\":\"" + AnswerSecret, doneReason: "stop")).Response), new OllamaOptions()),
            () => (FakeHandler.Returning(OllamaBodies.Response(Encoding.UTF8.GetBytes(AnswerSecret + " not json")).Response), new OllamaOptions()),
            () => (FakeHandler.Returning(OllamaBodies.Response(Encoding.UTF8.GetBytes(AnswerSecret), HttpStatusCode.InternalServerError).Response), new OllamaOptions()),
            () => (FakeHandler.Returning(OllamaBodies.Response(Encoding.UTF8.GetBytes(AnswerSecret + new string(' ', 5000))).Response), new OllamaOptions { MaxResponseBytes = 4096 }),
            () => (new FakeHandler((_, _) => throw new HttpRequestException("refused")), new OllamaOptions()),
        };

        foreach (var scenario in scenarios)
        {
            var (handler, options) = scenario();
            var (service, logs) = Create(handler, options);

            await Record.ExceptionAsync(() => service.GenerateResponseAsync(PromptSecret));

            foreach (var entry in logs.Entries)
            {
                Check.NotLeaked(entry.Message, PromptSecret, AnswerSecret);
                foreach (var value in entry.Fields.Values)
                {
                    Check.NotLeaked(value?.ToString() ?? "", PromptSecret, AnswerSecret);
                }

                if (entry.Exception is not null)
                {
                    Check.NotLeaked(entry.Exception.ToString(), PromptSecret, AnswerSecret);
                }
            }
        }
    }

    [Fact]
    public async Task TimeoutAndCancellation_NeverLogTheQuestionEither()
    {
        var hang = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });

        // timeout
        var (timeoutService, timeoutLogs) = Create(hang, new OllamaOptions { TimeoutSeconds = 1 });
        await Svc.FailureOf(timeoutService, PromptSecret).WaitAsync(TimeSpan.FromSeconds(10));

        // client cancellation
        var (cancelService, cancelLogs) = Create(hang);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelService.GenerateResponseAsync(PromptSecret, caller.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        foreach (var logs in new[] { timeoutLogs, cancelLogs })
        {
            Assert.NotEmpty(logs.Entries);
            foreach (var entry in logs.Entries)
            {
                Check.NotLeaked(entry.Message, PromptSecret);
                foreach (var value in entry.Fields.Values)
                {
                    Check.NotLeaked(value?.ToString() ?? "", PromptSecret);
                }
            }
        }
    }

    [Fact]
    public void EveryFieldIsANumber_ABoundedCategory_OrTheConfiguredModelOrAddress()
    {
        // The log templates are the only place the service writes diagnostics; keep them free of free text.
        var source = File.ReadAllText(FindSource("BC-LocalAI/Services/OllamaLog.cs"));

        foreach (var forbidden in new[] { "{Prompt", "{Message", "{Answer}", "{Response}", "{Body}", "{Text}", "{Content}", "{Payload}" })
        {
            Check.NotLeaked(source, forbidden);
        }
    }

    [Fact]
    public void EventIdsAreStable_SoDashboardsAndAlertsKeepWorking()
    {
        var source = File.ReadAllText(FindSource("BC-LocalAI/Services/OllamaLog.cs"));

        for (var id = 4001; id <= 4007; id++)
        {
            Assert.Contains($"EventId = {id}", source);
        }
    }

    private static string FindSource(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}")
            : Path.Combine(directory.FullName, relative);
    }
}
