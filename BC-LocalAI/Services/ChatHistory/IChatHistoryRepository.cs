using BC_LocalAI.Models.ChatHistory;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>
/// Storage for conversations and messages (BE-007 groundwork; no database implementation yet).
///
/// Ownership is part of every call: each method takes the user id taken from the authenticated session (BE-005 / BE-006),
/// never from the request body. A conversation that does not exist and one that belongs to someone else give the same
/// result (null / false), so a caller cannot find out which ids exist (IDOR protection).
/// Deleted conversations behave as if they do not exist.
/// Implementations must compare opaque user ids exactly (case and trailing spaces matter), without trimming/normalizing them.
/// SQL predicates must enforce ownership and non-deletion inside the transaction, not only in a preceding read.
/// UUID ordering/serialization is canonical network byte order, consistent with Guid.CompareTo; timestamps are persisted/read in UTC microseconds.
/// Add/Update must enforce ChatHistoryRules, bound content/cursors, allocate sequence numbers atomically and update conversation activity.
/// After a durable commit, cancellation must not be reported as proof that the write was rolled back; reconcile by message id.
/// This is a draft persistence contract: authentication claims and production API wiring still require BE-005/BE-006 approval.
/// </summary>
public interface IChatHistoryRepository
{
    /// <exception cref="ChatHistoryValidationException">The title is invalid (see <see cref="ChatHistoryRules.NormalizeTitle"/>).</exception>
    Task<Conversation> CreateConversationAsync(string userId, string title, CancellationToken cancellationToken = default);

    /// <summary>The user's conversations, most recently updated first.</summary>
    /// <exception cref="ChatHistoryValidationException">The limit or the cursor is invalid.</exception>
    Task<Page<Conversation>> ListConversationsAsync(string userId, PageRequest page, CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default);

    /// <returns>The renamed conversation, or null when it is not found for this user.</returns>
    /// <exception cref="ChatHistoryValidationException">The title is invalid.</exception>
    Task<Conversation?> RenameConversationAsync(string userId, Guid conversationId, string title, CancellationToken cancellationToken = default);

    /// <returns>false when it is not found for this user (also when it was already deleted).</returns>
    Task<bool> DeleteConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message at the end of the conversation. A user message is stored as <see cref="MessageStatus.Completed"/>;
    /// an assistant message starts as <see cref="MessageStatus.Pending"/> and is finished with <see cref="UpdateMessageAsync"/>.
    /// </summary>
    /// <returns>The message, or null when the conversation is not found for this user.</returns>
    /// <exception cref="ChatHistoryValidationException">A user message with empty content.</exception>
    Task<ChatMessage?> AddMessageAsync(
        string userId, Guid conversationId, MessageRole role, string content, CancellationToken cancellationToken = default);

    /// <summary>Moves an assistant message to its next state (see <see cref="ChatHistoryRules.EnsureTransition"/>).</summary>
    /// <returns>The updated message, or null when it is not found for this user.</returns>
    /// <exception cref="ChatHistoryValidationException">The transition is not allowed.</exception>
    Task<ChatMessage?> UpdateMessageAsync(
        string userId, Guid messageId, string content, MessageStatus status, string? errorCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>Messages of a conversation in conversation order (oldest first), or null when it is not found for this user.</summary>
    /// <exception cref="ChatHistoryValidationException">The limit or the cursor is invalid.</exception>
    Task<Page<ChatMessage>?> ListMessagesAsync(
        string userId, Guid conversationId, PageRequest page, CancellationToken cancellationToken = default);
}
