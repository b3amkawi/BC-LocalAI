using System.Data;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models.ChatHistory;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>The history database cannot be used right now. Maps to 503 HISTORY_UNAVAILABLE; never falls back to memory.</summary>
public sealed class ChatHistoryUnavailableException() : Exception("Chat history storage is unavailable.");

/// <summary>
/// <see cref="IChatHistoryRepository"/> on MySQL (database/chat-history/001-initial.up.sql), with the history runtime account
/// (SELECT/INSERT/UPDATE on the two tables). Every statement filters by the owner and DeletedAt inside the same statement or
/// transaction; all values are parameters. Ids are BINARY(16) in RFC (big-endian) byte order, which sorts like Guid.CompareTo;
/// times are UTC truncated to microseconds before they are stored or returned, so cursors match what the database holds.
/// </summary>
public sealed class MySqlChatHistoryRepository(IOptions<ChatHistoryOptions> options, TimeProvider clock) : IChatHistoryRepository
{
    private const int MaxUserIdLength = 64;
    private const string ConversationColumns = "c.Id, c.UserId, c.Title, c.CreatedAt, c.UpdatedAt";
    private const string MessageColumns = "m.Id, m.ConversationId, m.Sequence, m.Role, m.Content, m.Status, m.ErrorCode, m.CreatedAt, m.UpdatedAt";
    private const string Owned = "c.Id = @conversation AND c.UserId = @user AND c.DeletedAt IS NULL";

    public Task<Conversation> CreateConversationAsync(string userId, string title, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, async connection =>
        {
            EnsureUser(userId);
            var normalized = ChatHistoryRules.NormalizeTitle(title);
            var now = Now();
            var conversation = new Conversation(Guid.NewGuid(), userId, normalized, now, now);
            await using var command = Command(connection, null,
                "INSERT INTO Conversations (Id, UserId, Title, CreatedAt, UpdatedAt) VALUES (@conversation, @user, @title, @now, @now)");
            Add(command, conversation.Id, userId);
            command.Parameters.Add("@title", MySqlDbType.VarChar).Value = normalized;
            command.Parameters.Add("@now", MySqlDbType.DateTime).Value = now.UtcDateTime;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            return conversation;
        }, beforeOpen: () =>
        {
            EnsureUser(userId);
            ChatHistoryRules.NormalizeTitle(title);
        });

    public Task<Page<Conversation>> ListConversationsAsync(string userId, PageRequest page, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, async connection =>
        {
            var after = ChatHistoryCursor.ReadConversation(page.Cursor);
            await using var command = Command(connection, null,
                $"SELECT {ConversationColumns} FROM Conversations c WHERE c.UserId = @user AND c.DeletedAt IS NULL" +
                (after is null ? "" : " AND (c.UpdatedAt < @after OR (c.UpdatedAt = @after AND c.Id < @afterId))") +
                " ORDER BY c.UpdatedAt DESC, c.Id DESC LIMIT @take");
            command.Parameters.Add("@user", MySqlDbType.VarChar).Value = userId;
            if (after is { } position)
            {
                command.Parameters.Add("@after", MySqlDbType.DateTime).Value = position.UpdatedAt.UtcDateTime;
                command.Parameters.Add("@afterId", MySqlDbType.Binary).Value = position.Id.ToByteArray(bigEndian: true);
            }

            command.Parameters.Add("@take", MySqlDbType.Int32).Value = page.Limit + 1;
            var rows = await ReadConversations(command, cancellationToken);
            var items = rows.Take(page.Limit).ToList();
            return new Page<Conversation>(items,
                rows.Count > page.Limit ? ChatHistoryCursor.ForConversation(items[^1].UpdatedAt, items[^1].Id) : null);
        }, beforeOpen: () =>
        {
            EnsureUser(userId);
            ChatHistoryRules.EnsureValidPage(page);
            ChatHistoryCursor.ReadConversation(page.Cursor);
        });

    public Task<Conversation?> GetConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        !IsUser(userId) ? WhenCancelled<Conversation?>(cancellationToken, null) : Run(cancellationToken, connection =>
            FindConversation(connection, null, userId, conversationId, lockRow: false, cancellationToken));

    public Task<Conversation?> RenameConversationAsync(
        string userId, Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        var normalized = ChatHistoryRules.NormalizeTitle(title);
        return !IsUser(userId) ? WhenCancelled<Conversation?>(cancellationToken, null) : InTransaction(cancellationToken, async (connection, transaction) =>
        {
            var current = await FindConversation(connection, transaction, userId, conversationId, lockRow: true, cancellationToken);
            if (current is null) return null;
            var now = Now();
            await using var command = Command(connection, transaction,
                "UPDATE Conversations c SET c.Title = @title, c.UpdatedAt = @now WHERE " + Owned);
            Add(command, conversationId, userId);
            command.Parameters.Add("@title", MySqlDbType.VarChar).Value = normalized;
            command.Parameters.Add("@now", MySqlDbType.DateTime).Value = now.UtcDateTime;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return current with { Title = normalized, UpdatedAt = now };
        });
    }

    public Task<bool> DeleteConversationAsync(string userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        !IsUser(userId) ? WhenCancelled(cancellationToken, false) : Run(cancellationToken, async connection =>
        {
            // Soft delete: the conversation and its messages disappear from every owner query; nothing is erased (no DELETE grant).
            await using var command = Command(connection, null, "UPDATE Conversations c SET c.DeletedAt = @now WHERE " + Owned);
            Add(command, conversationId, userId);
            command.Parameters.Add("@now", MySqlDbType.DateTime).Value = Now().UtcDateTime;
            return await command.ExecuteNonQueryAsync(CancellationToken.None) == 1;
        });

    public Task<ChatMessage?> AddMessageAsync(
        string userId, Guid conversationId, MessageRole role, string content, CancellationToken cancellationToken = default)
    {
        ChatHistoryRules.EnsureValidMessage(role, content);
        return !IsUser(userId) ? WhenCancelled<ChatMessage?>(cancellationToken, null) : InTransaction(cancellationToken, async (connection, transaction) =>
        {
            // The owner-filtered row lock serializes sequence numbers of one conversation; the unique key is only a backstop.
            long next;
            await using (var select = Command(connection, transaction,
                             "SELECT c.NextSequence FROM Conversations c WHERE " + Owned + " FOR UPDATE"))
            {
                Add(select, conversationId, userId);
                if (await select.ExecuteScalarAsync(cancellationToken) is not long sequence) return null;
                next = sequence;
            }

            var now = Now();
            var message = new ChatMessage(Guid.NewGuid(), conversationId, next, role, content,
                role == MessageRole.User ? MessageStatus.Completed : MessageStatus.Pending, null, now, now);
            await using (var insert = Command(connection, transaction,
                             "INSERT INTO Messages (Id, ConversationId, Sequence, Role, Content, Status, ErrorCode, CreatedAt, UpdatedAt) " +
                             "VALUES (@id, @conversation, @sequence, @role, @content, @status, NULL, @now, @now)"))
            {
                insert.Parameters.Add("@id", MySqlDbType.Binary).Value = message.Id.ToByteArray(bigEndian: true);
                insert.Parameters.Add("@conversation", MySqlDbType.Binary).Value = conversationId.ToByteArray(bigEndian: true);
                insert.Parameters.Add("@sequence", MySqlDbType.Int64).Value = next;
                insert.Parameters.Add("@role", MySqlDbType.VarChar).Value = RoleText(role);
                insert.Parameters.Add("@content", MySqlDbType.MediumText).Value = content;
                insert.Parameters.Add("@status", MySqlDbType.VarChar).Value = StatusText(message.Status);
                insert.Parameters.Add("@now", MySqlDbType.DateTime).Value = now.UtcDateTime;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await Touch(connection, transaction, conversationId, now, nextSequence: true, cancellationToken);
            return message;
        });
    }

    public Task<ChatMessage?> UpdateMessageAsync(
        string userId, Guid messageId, string content, MessageStatus status, string? errorCode = null,
        CancellationToken cancellationToken = default) =>
        !IsUser(userId) ? WhenCancelled<ChatMessage?>(cancellationToken, null) : InTransaction(cancellationToken, async (connection, transaction) =>
        {
            ChatMessage? current;
            await using (var select = Command(connection, transaction,
                             $"SELECT {MessageColumns} FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId " +
                             "WHERE m.Id = @id AND c.UserId = @user AND c.DeletedAt IS NULL FOR UPDATE"))
            {
                select.Parameters.Add("@id", MySqlDbType.Binary).Value = messageId.ToByteArray(bigEndian: true);
                select.Parameters.Add("@user", MySqlDbType.VarChar).Value = userId;
                current = (await ReadMessages(select, cancellationToken)).SingleOrDefault();
            }

            if (current is null) return null;
            ChatHistoryRules.EnsureTransition(current, status, content, errorCode);
            var now = Now();
            await using (var update = Command(connection, transaction,
                             "UPDATE Messages SET Content = @content, Status = @status, ErrorCode = @error, UpdatedAt = @now " +
                             "WHERE Id = @id AND Status = @current"))
            {
                update.Parameters.Add("@id", MySqlDbType.Binary).Value = messageId.ToByteArray(bigEndian: true);
                update.Parameters.Add("@content", MySqlDbType.MediumText).Value = content;
                update.Parameters.Add("@status", MySqlDbType.VarChar).Value = StatusText(status);
                update.Parameters.Add("@error", MySqlDbType.VarChar).Value = (object?)errorCode ?? DBNull.Value;
                update.Parameters.Add("@now", MySqlDbType.DateTime).Value = now.UtcDateTime;
                update.Parameters.Add("@current", MySqlDbType.VarChar).Value = StatusText(current.Status);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ChatHistoryUnavailableException();
            }

            await Touch(connection, transaction, current.ConversationId, now, nextSequence: false, cancellationToken);
            return current with { Content = content, Status = status, ErrorCode = errorCode, UpdatedAt = now };
        });

    public Task<Page<ChatMessage>?> ListMessagesAsync(
        string userId, Guid conversationId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ChatHistoryRules.EnsureValidPage(page);
        var after = ChatHistoryCursor.ReadMessage(page.Cursor);
        return !IsUser(userId) ? WhenCancelled<Page<ChatMessage>?>(cancellationToken, null) : InTransaction(cancellationToken, async (connection, transaction) =>
        {
            // One snapshot: the ownership check and the page come from the same transaction.
            if (await FindConversation(connection, transaction, userId, conversationId, lockRow: false, cancellationToken) is null)
                return null;
            await using var command = Command(connection, transaction,
                $"SELECT {MessageColumns} FROM Messages m WHERE m.ConversationId = @conversation AND m.Sequence > @after " +
                "ORDER BY m.Sequence LIMIT @take");
            command.Parameters.Add("@conversation", MySqlDbType.Binary).Value = conversationId.ToByteArray(bigEndian: true);
            command.Parameters.Add("@after", MySqlDbType.Int64).Value = after;
            command.Parameters.Add("@take", MySqlDbType.Int32).Value = page.Limit + 1;
            var rows = await ReadMessages(command, cancellationToken);
            var items = rows.Take(page.Limit).ToList();
            return (Page<ChatMessage>?)new Page<ChatMessage>(items,
                rows.Count > page.Limit ? ChatHistoryCursor.ForMessage(items[^1].Sequence) : null);
        });
    }

    public Task<IReadOnlyList<ChatMessage>?> GetRecentMessagesAsync(
        string userId, Guid conversationId, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, ChatHistoryRules.MaxRecentMessages);
        return !IsUser(userId) ? WhenCancelled<IReadOnlyList<ChatMessage>?>(cancellationToken, null) : InTransaction(cancellationToken, async (connection, transaction) =>
        {
            if (await FindConversation(connection, transaction, userId, conversationId, lockRow: false, cancellationToken) is null)
                return null;
            await using var command = Command(connection, transaction,
                $"SELECT {MessageColumns} FROM Messages m WHERE m.ConversationId = @conversation ORDER BY m.Sequence DESC LIMIT @take");
            command.Parameters.Add("@conversation", MySqlDbType.Binary).Value = conversationId.ToByteArray(bigEndian: true);
            command.Parameters.Add("@take", MySqlDbType.Int32).Value = maxCount;
            var newestFirst = await ReadMessages(command, cancellationToken);
            newestFirst.Reverse();
            return (IReadOnlyList<ChatMessage>?)newestFirst;
        });
    }

    // ------------------------------------------------------------------ helpers

    private DateTimeOffset Now()
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero); // DATETIME(6): microseconds
    }

    private static bool IsUser(string? userId) => !string.IsNullOrWhiteSpace(userId) && userId.Length <= MaxUserIdLength;

    private static void EnsureUser(string userId)
    {
        if (!IsUser(userId))
            throw new ArgumentException("A user id from the authenticated session is required.", nameof(userId));
    }

    private static Task<T> WhenCancelled<T>(CancellationToken cancellationToken, T value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value);
    }

    private async Task<T> Run<T>(CancellationToken cancellationToken, Func<MySqlConnection, Task<T>> work, Action? beforeOpen = null)
    {
        beforeOpen?.Invoke(); // validation errors before any database work
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return await work(connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is MySqlException or TimeoutException or OperationCanceledException
                                              or InvalidOperationException or InvalidCastException or FormatException)
        {
            // Never pass on a database exception: it can carry SQL, connection details or message text.
            throw new ChatHistoryUnavailableException();
        }
    }

    private Task<T> InTransaction<T>(CancellationToken cancellationToken, Func<MySqlConnection, MySqlTransaction, Task<T>> work) =>
        Run(cancellationToken, async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await work(connection, transaction);
            // Not cancellable: once the work is done, the commit must not be abandoned half-way and reported as rolled back.
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        });

    private MySqlCommand Command(MySqlConnection connection, MySqlTransaction? transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = options.Value.CommandTimeoutSeconds };

    private static void Add(MySqlCommand command, Guid conversationId, string userId)
    {
        command.Parameters.Add("@conversation", MySqlDbType.Binary).Value = conversationId.ToByteArray(bigEndian: true);
        command.Parameters.Add("@user", MySqlDbType.VarChar).Value = userId;
    }

    private async Task<Conversation?> FindConversation(
        MySqlConnection connection, MySqlTransaction? transaction, string userId, Guid conversationId, bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            $"SELECT {ConversationColumns} FROM Conversations c WHERE {Owned}" + (lockRow ? " FOR UPDATE" : ""));
        Add(command, conversationId, userId);
        return (await ReadConversations(command, cancellationToken)).SingleOrDefault();
    }

    private async Task Touch(MySqlConnection connection, MySqlTransaction transaction, Guid conversationId, DateTimeOffset now,
        bool nextSequence, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE Conversations SET UpdatedAt = @now" + (nextSequence ? ", NextSequence = NextSequence + 1" : "") + " WHERE Id = @conversation");
        command.Parameters.Add("@conversation", MySqlDbType.Binary).Value = conversationId.ToByteArray(bigEndian: true);
        command.Parameters.Add("@now", MySqlDbType.DateTime).Value = now.UtcDateTime;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<Conversation>> ReadConversations(MySqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<Conversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Conversation(ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), Utc(reader, 3), Utc(reader, 4)));
        }

        return list;
    }

    private static async Task<List<ChatMessage>> ReadMessages(MySqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ChatMessage(
                ReadGuid(reader, 0), ReadGuid(reader, 1), reader.GetInt64(2), ParseRole(reader.GetString(3)), reader.GetString(4),
                ParseStatus(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6), Utc(reader, 7), Utc(reader, 8)));
        }

        return list;
    }

    private static Guid ReadGuid(MySqlDataReader reader, int ordinal)
    {
        var bytes = new byte[16];
        if (reader.GetBytes(ordinal, 0, bytes, 0, 16) != 16) throw new FormatException("Unexpected id length.");
        return new Guid(bytes, bigEndian: true);
    }

    private static DateTimeOffset Utc(MySqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static string RoleText(MessageRole role) => role == MessageRole.User ? "user" : "assistant";

    private static MessageRole ParseRole(string value) => value switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        _ => throw new FormatException("Unknown role.")
    };

    internal static string StatusText(MessageStatus status) => status.ToString().ToLowerInvariant();

    private static MessageStatus ParseStatus(string value) => value switch
    {
        "pending" => MessageStatus.Pending,
        "streaming" => MessageStatus.Streaming,
        "completed" => MessageStatus.Completed,
        "failed" => MessageStatus.Failed,
        "cancelled" => MessageStatus.Cancelled,
        _ => throw new FormatException("Unknown status.")
    };
}
