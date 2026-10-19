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
    [InlineData("https://ollama.internal:11434")]
    public void ValidBaseUrls_Pass(string url) =>
        Assert.True(IsValid(new OllamaOptions { BaseUrl = url }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("/relative")]
    public void InvalidBaseUrls_Fail(string url) =>
        Assert.False(IsValid(new OllamaOptions { BaseUrl = url }));

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
