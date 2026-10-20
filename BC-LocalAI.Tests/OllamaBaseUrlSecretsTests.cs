using BC_LocalAI.Configuration;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

/// <summary>
/// Review of PR #19: a user name or password written in Ollama:BaseUrl must never reach a log.
/// Two independent defences are tested: the configuration refuses such a URL, and the logs only ever describe the
/// endpoint as scheme://host:port even if a bad URL got through.
/// </summary>
public sealed class OllamaBaseUrlSecretsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "S3cr3t-pw-42";
    private const string User = "svc-user-77";

    public static TheoryData<string> UrlsWithCredentials =>
    [
        $"http://{User}:{Secret}@127.0.0.1:11434",
        $"http://{User}:{Secret}@127.0.0.1:11434/",
        $"http://{User}@localhost:11434",
        $"https://{User}:{Secret}@[::1]:11434",
        $"http://:{Secret}@127.0.0.1:11434",
        $"http://%75ser:{Secret}@127.0.0.1:11434",
        $"http://127.0.0.1:11434/{User}@host",
    ];

    /// <summary>The same URLs without the path-only case: a path is not a credential and the framework's own request log prints paths.</summary>
    public static TheoryData<string> UrlsWithUserInfo =>
    [
        $"http://{User}:{Secret}@127.0.0.1:11434",
        $"http://{User}@localhost:11434",
        $"https://{User}:{Secret}@[::1]:11434",
        $"http://:{Secret}@127.0.0.1:11434",
        $"http://%75ser:{Secret}@127.0.0.1:11434",
    ];

    private static bool IsValid(string url) =>
        new OllamaOptionsValidator().Validate(null, new OllamaOptions { BaseUrl = url }).Succeeded;

    private static void AssertNoSecrets(CapturingLoggerProvider logs)
    {
        Assert.NotEmpty(logs.Entries);
        foreach (var entry in logs.Entries)
        {
            Check.NotLeaked(entry.Message, Secret, User);
            foreach (var value in entry.Fields.Values)
            {
                Check.NotLeaked(value?.ToString() ?? "", Secret, User);
            }

            if (entry.Exception is not null)
            {
                Check.NotLeaked(entry.Exception.ToString(), Secret, User);
            }
        }
    }

    // ---------------------------------------------------------------- defence 1: the configuration refuses it

    [Theory]
    [MemberData(nameof(UrlsWithCredentials))]
    public void Validator_RejectsABaseUrlWithCredentials(string url) => Assert.False(IsValid(url));

    [Theory]
    [InlineData("http://127.0.0.1:1/REVIEW-PATH-SECRET")]
    [InlineData("http://127.0.0.1:11434/ollama")]
    [InlineData("http://127.0.0.1:11434/ollama/")]
    [InlineData("http://localhost:11434/a/b/c")]
    [InlineData("http://[::1]:11434/REVIEW-PATH-SECRET")]
    public void Validator_RejectsAPath(string url) => Assert.False(IsValid(url));

    [Theory]
    [InlineData("http://127.0.0.1:11434?token=abc")]
    [InlineData("http://127.0.0.1:11434/?key=abc")]
    [InlineData("http://127.0.0.1:11434/#fragment")]
    public void Validator_RejectsAQueryOrFragment(string url) => Assert.False(IsValid(url));

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("https://127.0.0.1:11434")]
    public void Validator_StillAcceptsCleanLoopbackUrls(string url) => Assert.True(IsValid(url));

    [Theory]
    [MemberData(nameof(UrlsWithCredentials))]
    public void TheFailureMessage_ExplainsTheRule_WithoutEchoingTheUrl(string url)
    {
        var result = new OllamaOptionsValidator().Validate(null, new OllamaOptions { BaseUrl = url });

        Assert.True(result.Failed);
        Assert.Contains("Ollama:BaseUrl must be only scheme, host and port (no credentials, path, query string or fragment).", result.Failures);
        foreach (var failure in result.Failures!)
        {
            Check.NotLeaked(failure, Secret, User);
        }
    }

    [Theory]
    [MemberData(nameof(UrlsWithCredentials))]
    public void TheApplication_WillNotStart_WithCredentialsInTheUrl_AndTheErrorDoesNotEchoThem(string url)
    {
        var logs = new CapturingLoggerProvider();
        var broken = factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama(baseUrl: url);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });

        var thrown = Assert.Throws<OptionsValidationException>(() => broken.CreateClient());

        Check.NotLeaked(thrown.ToString(), Secret, User);
        foreach (var entry in logs.Entries)
        {
            Check.NotLeaked(entry.Message, Secret, User);
        }
    }

    // ---------------------------------------------------------------- defence 2: logs only describe scheme://host:port

    [Theory]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434")]
    [InlineData("http://user:pw@127.0.0.1:11434/api?x=1#y", "http://127.0.0.1:11434")]
    [InlineData("https://u@localhost:8443/path/", "https://localhost:8443")]
    [InlineData("http://127.0.0.1:1/REVIEW-PATH-SECRET", "http://127.0.0.1:1")]
    [InlineData("http://u:p@[::1]:11434", "http://[::1]:11434")]
    [InlineData("http://127.0.0.1", "http://127.0.0.1:80")]
    [InlineData("not a url", "invalid")]
    [InlineData("", "invalid")]
    [InlineData("ftp://u:p@127.0.0.1", "invalid")]
    public void DescribeEndpoint_KeepsOnlySchemeHostAndPort(string baseUrl, string expected)
    {
        var described = OllamaOptions.DescribeEndpoint(baseUrl);

        Assert.Equal(expected, described);
        Check.NotLeaked(described, "pw", "user", "path", "api", "x=1");
    }

    [Fact]
    public async Task EvenIfABadUrlGetsThrough_TheServiceLogsOnlyTheEndpoint()
    {
        // A real connection attempt to a port nobody listens on, so the real .NET exception text is what gets logged.
        var options = new OllamaOptions { BaseUrl = $"http://{User}:{Secret}@127.0.0.1:59999" };
        var logs = new CapturingLoggerProvider();
        var service = Svc.Create(new SocketsHttpHandler(), options, logs.CreateLogger<OllamaAIService>());

        var failure = await Svc.FailureOf(service);

        Assert.Equal("AI_UNAVAILABLE", failure.Code);
        var entry = Assert.Single(logs.Entries, e => e.EventId.Id == 4004);
        Assert.Equal("http://127.0.0.1:59999", entry.Fields["Endpoint"]);
        Assert.False(entry.Fields.ContainsKey("BaseUrl"));
        AssertNoSecrets(logs);
        Check.NotLeaked(failure.ToString(), Secret, User);
    }

    [Theory]
    [InlineData("http://127.0.0.1:59999/REVIEW-PATH-SECRET")]
    [InlineData("http://127.0.0.1:59999/a/REVIEW-PATH-SECRET/b/")]
    [InlineData("http://127.0.0.1:59999/?key=REVIEW-PATH-SECRET")]
    [InlineData("http://127.0.0.1:59999/#REVIEW-PATH-SECRET")]
    public async Task TheWholeHost_NeverLogsASecretInThePathQueryOrFragment_EvenWhenTheValidationIsSwitchedOff(string url)
    {
        var logs = new CapturingLoggerProvider();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama(baseUrl: url);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.RemoveAll<IValidateOptions<OllamaOptions>>());
        });

        using var client = host.CreateClient();
        var response = await client.PostAsync(
            "/api/chat",
            new StringContent("""{"message":"hi"}""", System.Text.Encoding.UTF8, "application/json"));

        Check.NotLeaked(await response.Content.ReadAsStringAsync(), "REVIEW-PATH-SECRET");
        Assert.NotEmpty(logs.Entries);
        Assert.Contains(logs.Entries, e => e.Message.Contains("http://127.0.0.1:59999/api/generate")); // the framework log is there, without the secret
        foreach (var entry in logs.Entries)
        {
            Check.NotLeaked(entry.Message, "REVIEW-PATH-SECRET");
            foreach (var value in entry.Fields.Values) Check.NotLeaked(value?.ToString() ?? "", "REVIEW-PATH-SECRET");
            if (entry.Exception is not null) Check.NotLeaked(entry.Exception.ToString(), "REVIEW-PATH-SECRET");
        }
    }

    [Theory]
    [MemberData(nameof(UrlsWithUserInfo))]
    public async Task TheWholeHost_NeverLogsCredentials_EvenWhenTheValidationIsSwitchedOff(string url)
    {
        // Validation is replaced by "accept everything" so the rest of the stack (HttpClientFactory logging,
        // middleware, the service, the exception text) is exercised with a hostile URL.
        var logs = new CapturingLoggerProvider();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.PinOllama(baseUrl: url.Replace(":11434", ":59999"));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.RemoveAll<IValidateOptions<OllamaOptions>>());
        });

        using var client = host.CreateClient();
        var response = await client.PostAsync(
            "/api/chat",
            new StringContent("""{"message":"hi"}""", System.Text.Encoding.UTF8, "application/json"));

        var raw = await response.Content.ReadAsStringAsync();
        Check.NotLeaked(raw, Secret, User);
        AssertNoSecrets(logs);
    }
}
