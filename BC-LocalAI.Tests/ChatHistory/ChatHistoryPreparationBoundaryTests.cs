using System.Net;
using BC_LocalAI.Services.ChatHistory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.ChatHistory;

// Preparation-stage security gate: replace only after approved BE-005/006 runtime integration.
public sealed class ChatHistoryPreparationBoundaryTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task HistoryIsNotExposedWithoutTheRequiredIdentityContract(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
        });
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<IChatHistoryRepository>());
        Assert.Null(factory.Services.GetService<ConversationContextBuilder>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/conversations")).StatusCode);
    }
}
