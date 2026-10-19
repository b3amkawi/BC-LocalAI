using System.Net;
using System.Net.Http.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests;

public sealed class ChatApiProductionErrorTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(string environment) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
            builder.ConfigureTestServices(services =>
                services.AddScoped<IAIService, ThrowingAIService>());
        }).CreateClient();

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Post_WhenAIServiceThrows_Returns500JsonContract(string environment)
    {
        var client = CreateClient(environment);

        var response = await client.PostAsJsonAsync("/api/chat", new { message = "hello" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("INTERNAL_ERROR", body!.Error.Code);
        Assert.Equal("An unexpected error occurred", body.Error.Message);
    }

    [Fact]
    public async Task Post_WhenAIServiceThrows_InProduction_DoesNotLeakExceptionDetails()
    {
        var client = CreateClient("Production");

        var response = await client.PostAsJsonAsync("/api/chat", new { message = "hello" });
        var text = await response.Content.ReadAsStringAsync();

        Check.NotLeaked(text, "secret internal detail");
        Check.NotLeaked(text, "ThrowingAIService");
    }

    [Fact]
    public async Task Get_HomePage_InProduction_StillReturnsHtml()
    {
        var client = CreateClient("Production");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class ThrowingAIService : IAIService
    {
        public Task<ChatResponse> GenerateResponseAsync(
            string message,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret internal detail");
    }
}
