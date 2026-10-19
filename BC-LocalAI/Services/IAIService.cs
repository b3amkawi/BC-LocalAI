using BC_LocalAI.Models;

namespace BC_LocalAI.Services;

public interface IAIService
{
    Task<ChatResponse> GenerateResponseAsync(
        string message,
        CancellationToken cancellationToken = default);
}
