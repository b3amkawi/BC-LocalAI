using System.Globalization;
using System.Text;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>
/// Opaque keyset cursors. They carry a position only, never an owner: ownership is always the session's user id in the query.
/// Conversations: UpdatedAt (UTC ticks, microsecond values as stored) and Id; messages: Sequence.
/// </summary>
public static class ChatHistoryCursor
{
    public static string ForConversation(DateTimeOffset updatedAt, Guid id) =>
        Encode($"{updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}");

    public static (DateTimeOffset UpdatedAt, Guid Id)? ReadConversation(string? cursor)
    {
        if (cursor is null) return null;
        var parts = Decode(cursor).Split('|');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks is >= 0 and <= 3155378975999999999 // DateTime.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out var id))
        {
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        throw Invalid();
    }

    public static string ForMessage(long sequence) => Encode(sequence.ToString(CultureInfo.InvariantCulture));

    public static long ReadMessage(string? cursor) =>
        cursor is null ? 0
        : long.TryParse(Decode(cursor), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ? sequence
        : throw Invalid();

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string cursor)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    private static ChatHistoryValidationException Invalid() => new("INVALID_PAGE", "The cursor is not valid.");
}
