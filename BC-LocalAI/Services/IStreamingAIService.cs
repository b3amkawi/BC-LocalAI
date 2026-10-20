namespace BC_LocalAI.Services;

public interface IStreamingAIService
{
    Task<AiStreamCompletion> StreamResponseAsync(
        string message,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken = default);
}

public sealed record AiStreamCompletion(string FinishReason, int? EvalCount);
