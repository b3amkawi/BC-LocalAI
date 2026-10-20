using System.Text.Json;
using BC_LocalAI.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests;

/// <summary>BE-003 configuration: Ollama:MaxOutputTokens and Ollama:MaxResponseBytes (and the existing TimeoutSeconds).</summary>
public sealed class OllamaLimitOptionsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static bool IsValid(OllamaOptions options) =>
        new OllamaOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void Defaults_AreValid_AndAreTheDocumentedValues()
    {
        var options = new OllamaOptions();

        Assert.True(IsValid(options));
        Assert.Equal(180, options.TimeoutSeconds);
        Assert.Equal(1024, options.MaxOutputTokens);
        Assert.Equal(1_048_576, options.MaxResponseBytes);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(256, true)]
    [InlineData(1024, true)]
    [InlineData(4096, true)]
    [InlineData(15, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(4097, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void MaxOutputTokens_Range_IsEnforced(int tokens, bool expected) =>
        Assert.Equal(expected, IsValid(new OllamaOptions { MaxOutputTokens = tokens }));

    [Theory]
    [InlineData(4096, true)]
    [InlineData(1_048_576, true)]
    [InlineData(16_777_216, true)]
    [InlineData(4095, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(16_777_217, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void MaxResponseBytes_Range_IsEnforced(int bytes, bool expected) =>
        Assert.Equal(expected, IsValid(new OllamaOptions { MaxResponseBytes = bytes }));

    [Fact]
    public void TheFailureMessages_NameTheSettingAndTheRange()
    {
        var result = new OllamaOptionsValidator().Validate(
            null,
            new OllamaOptions { MaxOutputTokens = 0, MaxResponseBytes = 0, TimeoutSeconds = 0 });

        Assert.True(result.Failed);
        Assert.Contains("Ollama:MaxOutputTokens must be between 16 and 4096.", result.Failures);
        Assert.Contains("Ollama:MaxResponseBytes must be between 4096 and 16777216.", result.Failures);
        Assert.Contains("Ollama:TimeoutSeconds must be between 1 and 600.", result.Failures);
    }

    [Fact]
    public void EveryInvalidValue_IsReportedAtOnce()
    {
        var result = new OllamaOptionsValidator().Validate(
            null,
            new OllamaOptions { BaseUrl = "http://192.168.1.10:11434", Model = "", MaxOutputTokens = 1, MaxResponseBytes = 1, TimeoutSeconds = 0 });

        Assert.Equal(5, result.Failures!.Count());
    }

    // ---------------------------------------------------------------- the whole application

    [Theory]
    [InlineData("0", "1048576")]
    [InlineData("4097", "1048576")]
    [InlineData("-5", "1048576")]
    [InlineData("abc", "1048576")]
    [InlineData("1024", "100")]
    [InlineData("1024", "999999999")]
    public void InvalidLimitSettings_StopTheApplicationAtStartup(string tokens, string bytes)
    {
        var broken = factory.WithWebHostBuilder(builder => builder.PinOllama(maxOutputTokens: tokens, maxResponseBytes: bytes));

        // "abc" cannot even be bound to a number, so the host stops with another exception type.
        Assert.ThrowsAny<Exception>(() => broken.CreateClient());
    }

    [Fact]
    public void ValidLimitSettings_AreUsedByTheApplication()
    {
        var app = factory.WithWebHostBuilder(builder => builder.PinOllama(maxOutputTokens: "2048", maxResponseBytes: "2097152"));

        using var client = app.CreateClient();
        var options = app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;

        Assert.Equal(2048, options.MaxOutputTokens);
        Assert.Equal(2_097_152, options.MaxResponseBytes);
    }

    [Fact]
    public void ShippedAppsettings_ContainTheDocumentedLimits()
    {
        // Read the file itself so a machine-level Ollama__* variable cannot change the result.
        var environment = factory.WithWebHostBuilder(builder => builder.PinOllama())
            .Services.GetRequiredService<IWebHostEnvironment>();
        var json = File.ReadAllText(Path.Combine(environment.ContentRootPath, "appsettings.json"));
        using var document = JsonDocument.Parse(json);
        var ollama = document.RootElement.GetProperty("Ollama");

        Assert.Equal(180, ollama.GetProperty("TimeoutSeconds").GetInt32());
        Assert.Equal(1024, ollama.GetProperty("MaxOutputTokens").GetInt32());
        Assert.Equal(1_048_576, ollama.GetProperty("MaxResponseBytes").GetInt32());
    }
}
