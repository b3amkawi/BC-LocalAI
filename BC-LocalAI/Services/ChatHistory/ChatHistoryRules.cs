using BC_LocalAI.Models.ChatHistory;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>A value sent by the caller breaks a chat history rule. Maps to 400 when the API exists.</summary>
public sealed class ChatHistoryValidationException(string code, string message) : Exception(message)
{
    /// <summary>Stable machine-readable code, for example INVALID_TITLE.</summary>
    public string Code { get; } = code;
}

/// <summary>Business rules shared by every <see cref="IChatHistoryRepository"/> implementation.</summary>
public static class ChatHistoryRules
{
    public const int MaxTitleLength = 200;
    public const string DefaultTitle = "New chat";

    /// <summary>Trims the title; an empty title becomes <see cref="DefaultTitle"/>.</summary>
    /// <exception cref="ChatHistoryValidationException">Longer than <see cref="MaxTitleLength"/> characters or contains control characters.</exception>
    public static string NormalizeTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return DefaultTitle;
        }

        if (trimmed.Length > MaxTitleLength)
        {
            throw new ChatHistoryValidationException("INVALID_TITLE", $"Title must be at most {MaxTitleLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ChatHistoryValidationException("INVALID_TITLE", "Title must not contain control characters.");
        }

        return trimmed;
    }

    /// <exception cref="ChatHistoryValidationException">The limit is outside 1..<see cref="PageRequest.MaxLimit"/>.</exception>
    public static void EnsureValidPage(PageRequest page)
    {
        if (page.Limit is < 1 or > PageRequest.MaxLimit)
        {
            throw new ChatHistoryValidationException("INVALID_PAGE", $"Limit must be between 1 and {PageRequest.MaxLimit}.");
        }
    }

    /// <summary>
    /// Allowed changes of an assistant message:
    /// Pending -> Streaming | Completed | Failed | Cancelled;
    /// Streaming -> Streaming (more content) | Completed | Failed | Cancelled;
    /// Completed, Failed and Cancelled are final.
    /// Completed needs non-empty content; Failed needs an error code; going back to Pending is never allowed.
    /// User messages cannot be changed.
    /// </summary>
    /// <exception cref="ChatHistoryValidationException">The change is not allowed.</exception>
    public static void EnsureTransition(ChatMessage current, MessageStatus next, string content, string? errorCode)
    {
        if (current.Role != MessageRole.Assistant)
        {
            throw Invalid("Only assistant messages can be updated.");
        }

        if (IsFinal(current.Status))
        {
            throw Invalid($"A {current.Status} message cannot be changed.");
        }

        if (next == MessageStatus.Pending)
        {
            throw Invalid("A message cannot go back to Pending.");
        }

        if (next == MessageStatus.Completed && string.IsNullOrWhiteSpace(content))
        {
            throw Invalid("A completed message needs content.");
        }

        if (next == MessageStatus.Failed && string.IsNullOrWhiteSpace(errorCode))
        {
            throw Invalid("A failed message needs an error code.");
        }

        if (next != MessageStatus.Failed && errorCode is not null)
        {
            throw Invalid("Only a failed message has an error code.");
        }
    }

    public static bool IsFinal(MessageStatus status) =>
        status is MessageStatus.Completed or MessageStatus.Failed or MessageStatus.Cancelled;

    private static ChatHistoryValidationException Invalid(string message) => new("INVALID_MESSAGE_STATE", message);
}
