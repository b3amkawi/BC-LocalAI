using BC_LocalAI.Services;

namespace BC_LocalAI.Tests;

public sealed class MockAIServiceTests
{
    [Fact]
    public async Task GenerateResponseAsync_ReturnsMockContract()
    {
        var service = new MockAIService();

        var response = await service.GenerateResponseAsync("hello");

        Assert.Equal("Mock AI response", response.Response);
        Assert.Equal("mock", response.Model);
    }

    [Fact]
    public async Task GenerateResponseAsync_WithCancellation_Throws()
    {
        var service = new MockAIService();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GenerateResponseAsync("hello", cancellation.Token));
    }
}
