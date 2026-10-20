using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BC_LocalAI.Security;

/// <summary>
/// Server-side session store. Revocation here ends a session even if its cookie was copied.
/// The current implementation keeps sessions in memory (decision 2026-10-20): a restart signs everybody out,
/// and only a single application instance is supported. A MySQL-backed store can replace it later.
/// </summary>
public interface ISessionStore
{
    Task<SessionRecord> CreateAsync(string userId, string role, string securityStamp, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Records activity (idle timeout). Never changes CreatedAt, so the absolute lifetime cannot be extended.</summary>
    Task TouchAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <returns>false when the session does not exist or was already revoked.</returns>
    Task<bool> RevokeAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Ends every session of a user (account disabled, password reset, security incident).</summary>
    /// <returns>How many sessions were revoked.</returns>
    Task<int> RevokeAllForUserAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);

    /// <summary>256 random bits, URL-safe. Not derived from the user, the time or a counter.</summary>
    public static string NewSessionId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Task<SessionRecord> CreateAsync(string userId, string role, string securityStamp, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        if (!BcRoles.IsKnown(role))
        {
            throw new ArgumentException("Unknown role.", nameof(role));
        }

        while (true)
        {
            var session = new SessionRecord(NewSessionId(), userId, role, securityStamp, now, now, null);
            if (_sessions.TryAdd(session.SessionId, session))
            {
                return Task.FromResult(session);
            }
        }
    }

    public Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(sessionId is not null && _sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public Task TouchAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_sessions.TryGetValue(sessionId, out var current))
        {
            if (current.RevokedAt is not null || now <= current.LastSeenAt)
            {
                break;
            }

            if (_sessions.TryUpdate(sessionId, current with { LastSeenAt = now }, current))
            {
                break;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (sessionId is not null && _sessions.TryGetValue(sessionId, out var current))
        {
            if (current.RevokedAt is not null)
            {
                return Task.FromResult(false);
            }

            if (_sessions.TryUpdate(sessionId, current with { RevokedAt = now }, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public async Task<int> RevokeAllForUserAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var session in _sessions.Values.Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal)))
        {
            if (await RevokeAsync(session.SessionId, now, cancellationToken))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// The current state of an account, read on every authenticated request. BE-005 implements it with the MySQL
/// reader account (SEC-001 schema). Returning null means the account no longer exists.
/// Throwing means the store is unavailable: the request is then refused (fail closed).
/// </summary>
public interface IUserSecurityStateSource
{
    Task<UserSecurityState?> GetAsync(string userId, CancellationToken cancellationToken = default);
}
