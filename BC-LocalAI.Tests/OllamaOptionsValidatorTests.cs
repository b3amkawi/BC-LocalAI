using BC_LocalAI.Configuration;

namespace BC_LocalAI.Tests;

public sealed class OllamaOptionsValidatorTests
{
    private static bool IsValid(OllamaOptions options) =>
        new OllamaOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void Defaults_AreValid_AndLoopback()
    {
        var options = new OllamaOptions();

        Assert.True(IsValid(options));
        Assert.Equal("http://127.0.0.1:11434", options.BaseUrl);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("https://127.0.0.1:11434")]
    [InlineData("http://127.0.0.2:11434")]
    [InlineData("http://127.0.0.1:11434/")]
    public void LoopbackBaseUrls_Pass(string url) =>
        Assert.True(IsValid(new OllamaOptions { BaseUrl = url }));

    [Theory]
    [InlineData("https://ollama.internal:11434")]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("https://ollama.example.com")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://[::]:11434")]
    [InlineData("http://203.0.113.7:11434")]
    [InlineData("http://127.0.0.1.nip.io:11434")]
    [InlineData("http://localhost.example.com:11434")]
    [InlineData("http://user@ollama.example.com:11434")]
    public void NonLoopbackBaseUrls_Fail(string url) =>
        Assert.False(IsValid(new OllamaOptions { BaseUrl = url }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1:11434")]
    [InlineData("localhost:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("file:///c:/ollama")]
    [InlineData("/relative")]
    [InlineData("not a url")]
    public void MalformedOrNonHttpBaseUrls_Fail(string url) =>
        Assert.False(IsValid(new OllamaOptions { BaseUrl = url }));

    [Fact]
    public void NonLoopbackFailure_ExplainsLoopbackRequirement()
    {
        var result = new OllamaOptionsValidator().Validate(
            null,
            new OllamaOptions { BaseUrl = "http://192.168.1.10:11434" });

        Assert.True(result.Failed);
        Assert.Contains("Ollama:BaseUrl must be an absolute loopback http or https URL.", result.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyModel_Fails(string model) =>
        Assert.False(IsValid(new OllamaOptions { Model = model }));

    [Theory]
    [InlineData(1, true)]
    [InlineData(180, true)]
    [InlineData(600, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(601, false)]
    public void TimeoutRange_IsEnforced(int seconds, bool expected) =>
        Assert.Equal(expected, IsValid(new OllamaOptions { TimeoutSeconds = seconds }));
}
