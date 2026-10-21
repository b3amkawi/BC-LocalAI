using System.Globalization;
using System.Text;
using BC_LocalAI.Models.ChatHistory;
using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>
/// Reference implementation of <see cref="IChatHistoryRepository"/> used to pin down the contract before a database exists.
/// Not registered in the application.
/// </summary>
internal sealed class InMemoryChatHistoryRepository(Func<DateTimeOffset> clock) : IChatHistoryRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StoredConversation> _conversations = [];
    private readonly Dictionary<Guid, ChatMessage> _messages = [];

    public InMemoryChatHistoryRepository() : this(() => DateTimeOffset.UtcNow)
    {
    }

    public Task<Conversation> CreateConversationAsync(string userId, string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUser(userId);
        var normalized = ChatHistoryRules.NormalizeTitle(title);
        var now = clock();
        var conversation = new Conversation(Guid.NewGuid(), userId, normalized, now, now);
        lock (_gate)
        {
            _conversations[conversation.Id] = new StoredConversation(conversation, Deleted: false, NextSequence: 1);
        }

        return Task.FromResult(conversation);
    }

    public Task<Page<Conversation>> ListConversationsAsync(string userId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUser(userId);
        ChatHistoryRules.EnsureValidPage(page);
        var after = DecodeConversationCursor(page.Cursor);

        lock (_gate)
        {
            var ordered = _conversations.Values
                .Where(s => !s.Deleted && s.Conversation.UserId == userId)
                .Select(s => s.Conversation)
                .OrderByDescending(c => c.UpdatedAt)
                .ThenByDescending(c => c.Id)
                .Where(c => after is null
                            || c.UpdatedAt < after.Value.UpdatedAt
                            || (c.UpdatedAt == after.Value.UpdatedAt && c.Id.CompareTo(after.Value.Id) < 0))
                .Take(page.Limit + 1)
                .ToList();

            var items = ordered.Take(page.Limit).ToList();
            var next = ordered.Count > page.Limit ? EncodeConversationCursor(items[^1]) : null;
            return Task.FromResult(new Page<Conversation>(items, next));
        }
    }

    public Task<Conversation?> GetConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(FindOwned(userId, conversationId)?.Conversation);
        }
    }

    public Task<Conversation?> RenameConversationAsync(
        string userId, Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ChatHistoryRules.NormalizeTitle(title);
        lock (_gate)
        {
            var stored = FindOwned(userId, conversationId);
            if (stored is null)
            {
                return Task.FromResult<Conversation?>(null);
            }

            var renamed = stored.Conversation with { Title = normalized, UpdatedAt = clock() };
            _conversations[conversationId] = stored with { Conversation = renamed };
            return Task.FromResult<Conversation?>(renamed);
        }
    }

    public Task<bool> DeleteConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var stored = FindOwned(userId, conversationId);
            if (stored is null)
            {
                return Task.FromResult(false);
            }

            _conversations[conversationId] = stored with { Deleted = true };
            return Task.FromResult(true);
        }
    }

    public Task<ChatMessage?> AddMessageAsync(
        string userId, Guid conversationId, MessageRole role, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChatHistoryRules.EnsureValidMessage(role, content);

        lock (_gate)
        {
            var stored = FindOwned(userId, conversationId);
            if (stored is null)
            {
                return Task.FromResult<ChatMessage?>(null);
            }

            var now = clock();
            var status = role == MessageRole.User ? MessageStatus.Completed : MessageStatus.Pending;
            var message = new ChatMessage(
                Guid.NewGuid(), conversationId, stored.NextSequence, role, content ?? string.Empty, status, null, now, now);
            _messages[message.Id] = message;
            _conversations[conversationId] = stored with
            {
                NextSequence = stored.NextSequence + 1,
                Conversation = stored.Conversation with { UpdatedAt = now }
            };
            return Task.FromResult<ChatMessage?>(message);
        }
    }

    public Task<ChatTurnMessages?> AddTurnAsync(
        string userId, Guid conversationId, string question, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChatHistoryRules.EnsureValidMessage(MessageRole.User, question);
        lock (_gate)
        {
            var stored = FindOwned(userId, conversationId);
            if (stored is null)
            {
                return Task.FromResult<ChatTurnMessages?>(null);
            }

            var now = clock();
            var asked = new ChatMessage(Guid.NewGuid(), conversationId, stored.NextSequence, MessageRole.User, question,
                MessageStatus.Completed, null, now, now);
            var answer = new ChatMessage(Guid.NewGuid(), conversationId, stored.NextSequence + 1, MessageRole.Assistant, "",
                MessageStatus.Pending, null, now, now);
            _messages[asked.Id] = asked;
            _messages[answer.Id] = answer;
            _conversations[conversationId] = stored with
            {
                NextSequence = stored.NextSequence + 2,
                Conversation = stored.Conversation with { UpdatedAt = now }
            };
            return Task.FromResult<ChatTurnMessages?>(new ChatTurnMessages(asked, answer));
        }
    }

    public Task<ChatMessage?> UpdateMessageAsync(
        string userId, Guid messageId, string content, MessageStatus status, string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var current) || FindOwned(userId, current.ConversationId) is null)
            {
                return Task.FromResult<ChatMessage?>(null);
            }

            ChatHistoryRules.EnsureTransition(current, status, content, errorCode);
            var updated = current with { Content = content, Status = status, ErrorCode = errorCode, UpdatedAt = clock() };
            _messages[messageId] = updated;
            var conversation = _conversations[current.ConversationId];
            _conversations[current.ConversationId] = conversation with
            {
                Conversation = conversation.Conversation with { UpdatedAt = updated.UpdatedAt }
            };
            return Task.FromResult<ChatMessage?>(updated);
        }
    }

    public Task<Page<ChatMessage>?> ListMessagesAsync(
        string userId, Guid conversationId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChatHistoryRules.EnsureValidPage(page);
        var after = DecodeSequenceCursor(page.Cursor);

        lock (_gate)
        {
            if (FindOwned(userId, conversationId) is null)
            {
                return Task.FromResult<Page<ChatMessage>?>(null);
            }

            var ordered = _messages.Values
                .Where(m => m.ConversationId == conversationId && m.Sequence > after)
                .OrderBy(m => m.Sequence)
                .Take(page.Limit + 1)
                .ToList();

            var items = ordered.Take(page.Limit).ToList();
            var next = ordered.Count > page.Limit ? Encode(items[^1].Sequence.ToString(CultureInfo.InvariantCulture)) : null;
            return Task.FromResult<Page<ChatMessage>?>(new Page<ChatMessage>(items, next));
        }
    }

    public Task<IReadOnlyList<ChatMessage>?> GetRecentMessagesAsync(
        string userId, Guid conversationId, int maxCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, ChatHistoryRules.MaxRecentMessages);
        lock (_gate)
        {
            if (FindOwned(userId, conversationId) is null)
            {
                return Task.FromResult<IReadOnlyList<ChatMessage>?>(null);
            }

            IReadOnlyList<ChatMessage> recent = _messages.Values
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Sequence)
                .Take(maxCount)
                .OrderBy(m => m.Sequence)
                .ToList();
            return Task.FromResult<IReadOnlyList<ChatMessage>?>(recent);
        }
    }

    private StoredConversation? FindOwned(string userId, Guid conversationId) =>
        !string.IsNullOrWhiteSpace(userId)
        && _conversations.TryGetValue(conversationId, out var stored)
        && !stored.Deleted
        && stored.Conversation.UserId == userId
            ? stored
            : null;

    private static void EnsureUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A user id from the authenticated session is required.", nameof(userId));
        }
    }

    private static string EncodeConversationCursor(Conversation last) =>
        Encode($"{last.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{last.Id:N}");

    private static (DateTimeOffset UpdatedAt, Guid Id)? DecodeConversationCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var parts = Decode(cursor).Split('|');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks is >= 0 and <= 3155378975999999999 // DateTime.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out var id))
        {
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        throw InvalidCursor();
    }

    private static long DecodeSequenceCursor(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        return long.TryParse(Decode(cursor), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : throw InvalidCursor();
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string cursor)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static ChatHistoryValidationException InvalidCursor() => new("INVALID_PAGE", "The cursor is not valid.");

    private sealed record StoredConversation(Conversation Conversation, bool Deleted, long NextSequence);
}
