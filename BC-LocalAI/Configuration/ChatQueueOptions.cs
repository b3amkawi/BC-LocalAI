using Microsoft.Extensions.Options;

namespace BC_LocalAI.Configuration;

/// <summary>
/// BE-008: admission control in front of Ollama, shared by /api/chat and /api/chat/stream. The defaults are PROPOSALS taken from
/// the AI-006 measurements and the AI-007 policy (Ollama answers one request at a time with NUM_PARALLEL=1); none of them is
/// an approved capacity figure. See docs/BE-008-CONFIGURATION.md.
/// </summary>
public sealed class ChatQueueOptions
{
    public const string SectionName = "ChatQueue";

    /// <summary>Requests sent to Ollama at the same time. 1 matches Ollama's default NUM_PARALLEL=1; raise it only together with that setting.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>Requests allowed to wait for a free slot. A request that finds the queue full is refused at once with 503.</summary>
    public int MaxQueueLength { get; set; } = 8;

    /// <summary>Longest time a request may wait for a slot, separate from <c>Ollama:TimeoutSeconds</c> (which starts only when it is served).</summary>
    public int MaxQueueWaitSeconds { get; set; } = 30;

    /// <summary>Requests one signed-in user may have in flight (being served plus waiting). The next one is refused with 429.</summary>
    public int MaxPendingPerUser { get; set; } = 1;

    /// <summary>Fixed guidance sent as Retry-After with 429/503. A constant, not a prediction of when a slot frees.</summary>
    public int RetryAfterSeconds { get; set; } = 10;

    /// <summary>
    /// Adds the proposed <c>queued</c> and <c>processing</c> events to the stream. Off until FE-004 and the owners accept
    /// that contract change; with it off, streams look exactly as they did before BE-008.
    /// </summary>
    public bool EmitQueueEvents { get; set; }
}

public sealed class ChatQueueOptionsValidator : IValidateOptions<ChatQueueOptions>
{
    public const int MaxConcurrentLimit = 16;
    public const int MaxQueueLengthLimit = 256;
    public const int MaxQueueWaitLimitSeconds = 300;
    public const int MaxPendingPerUserLimit = 16;
    public const int MaxRetryAfterLimitSeconds = 300;

    public ValidateOptionsResult Validate(string? name, ChatQueueOptions options)
    {
        var failures = new List<string>();

        if (options.MaxConcurrentRequests is < 1 or > MaxConcurrentLimit)
            failures.Add($"ChatQueue:MaxConcurrentRequests must be between 1 and {MaxConcurrentLimit}.");
        if (options.MaxQueueLength is < 0 or > MaxQueueLengthLimit)
            failures.Add($"ChatQueue:MaxQueueLength must be between 0 and {MaxQueueLengthLimit}.");
        if (options.MaxQueueWaitSeconds is < 1 or > MaxQueueWaitLimitSeconds)
            failures.Add($"ChatQueue:MaxQueueWaitSeconds must be between 1 and {MaxQueueWaitLimitSeconds}.");
        if (options.MaxPendingPerUser is < 1 or > MaxPendingPerUserLimit)
            failures.Add($"ChatQueue:MaxPendingPerUser must be between 1 and {MaxPendingPerUserLimit}.");
        if (options.RetryAfterSeconds is < 1 or > MaxRetryAfterLimitSeconds)
            failures.Add($"ChatQueue:RetryAfterSeconds must be between 1 and {MaxRetryAfterLimitSeconds}.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
