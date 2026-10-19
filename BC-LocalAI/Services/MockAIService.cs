using BC_LocalAI.Models;

namespace BC_LocalAI.Services;

public sealed class MockAIService : IAIService
{
    public Task<ChatResponse> GenerateResponseAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ChatResponse("Mock AI response", "mock"));
    }
}
