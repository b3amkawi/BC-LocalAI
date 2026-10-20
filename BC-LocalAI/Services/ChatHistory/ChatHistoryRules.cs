using System.Text;
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
    public const int MaxCursorLength = 256;
    // Draft storage ceiling only. The approved AI input/output budgets may be lower.
    public const int MaxMessageBytes = 1_048_576;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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

        EnsureUtf8(trimmed, "INVALID_TITLE");

        return trimmed;
    }

    /// <exception cref="ChatHistoryValidationException">The limit is outside 1..<see cref="PageRequest.MaxLimit"/>.</exception>
    public static void EnsureValidPage(PageRequest page)
    {
        if (page is null || page.Limit is < 1 or > PageRequest.MaxLimit)
        {
            throw new ChatHistoryValidationException("INVALID_PAGE", $"Limit must be between 1 and {PageRequest.MaxLimit}.");
        }

        if (page.Cursor is not null && (page.Cursor.Length is 0 or > MaxCursorLength || page.Cursor.Any(char.IsWhiteSpace)))
            throw new ChatHistoryValidationException("INVALID_PAGE", "The cursor is not valid.");
    }

    public static void EnsureValidMessage(MessageRole role, string? content)
    {
        if (!Enum.IsDefined(role))
            throw new ChatHistoryValidationException("INVALID_MESSAGE", "The message role is not valid.");
        if (content is null || (role == MessageRole.User && string.IsNullOrWhiteSpace(content)))
            throw new ChatHistoryValidationException("INVALID_MESSAGE", "Message content is required.");
        if (EnsureUtf8(content, "INVALID_MESSAGE") > MaxMessageBytes)
            throw new ChatHistoryValidationException("INVALID_MESSAGE", "Message content exceeds the storage limit.");
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
        if (!Enum.IsDefined(current.Status) || !Enum.IsDefined(next))
            throw Invalid("The message status is not valid.");

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

        if (errorCode is not null && (errorCode.Length > 64 || !errorCode.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')))
            throw Invalid("The error code is not valid.");

        if (next != MessageStatus.Failed && errorCode is not null)
        {
            throw Invalid("Only a failed message has an error code.");
        }

        EnsureValidMessage(current.Role, content);
    }

    public static bool IsFinal(MessageStatus status) =>
        status is MessageStatus.Completed or MessageStatus.Failed or MessageStatus.Cancelled;

    private static ChatHistoryValidationException Invalid(string message) => new("INVALID_MESSAGE_STATE", message);

    private static int EnsureUtf8(string text, string code)
    {
        try { return StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw new ChatHistoryValidationException(code, "Text must contain valid Unicode."); }
    }
}
