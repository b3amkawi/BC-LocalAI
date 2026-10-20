using BC_LocalAI.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using AuthenticationOptions = BC_LocalAI.Configuration.AuthenticationOptions;

namespace BC_LocalAI.Services.Authentication;

/// <summary>Bounded, single-instance BE-005 sessions. Restart invalidates all tickets. Durable/revalidation work is BE-006.</summary>
public sealed class BasicAuthenticationTicketStore(IOptions<AuthenticationOptions> options, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> sessions = new(StringComparer.Ordinal);
    private sealed record Entry(byte[] Ticket, DateTimeOffset AbsoluteExpiry, DateTimeOffset IdleExpiry);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in sessions.Where(s => s.Value.IdleExpiry <= now || s.Value.AbsoluteExpiry <= now).Select(s => s.Key).ToArray())
                sessions.Remove(expired);
            if (sessions.Count >= options.Value.MaxSessions) throw new AuthenticationDependencyException();
            var key = Guid.NewGuid().ToString("N");
            sessions.Add(key, Create(ticket, now + AbsoluteLifetime, now));
            return Task.FromResult(key);
        }
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (sessions.TryGetValue(key, out var existing) && existing.IdleExpiry > now && existing.AbsoluteExpiry > now)
                sessions[key] = Create(ticket, existing.AbsoluteExpiry, now);
            else sessions.Remove(key); // Logout/expiry racing renewal must never recreate a revoked session.
        }
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (sessions.TryGetValue(key, out var entry) && entry.IdleExpiry > now && entry.AbsoluteExpiry > now)
                return Task.FromResult<AuthenticationTicket?>(TicketSerializer.Default.Deserialize(entry.Ticket));
            sessions.Remove(key);
            return Task.FromResult<AuthenticationTicket?>(null);
        }
    }

    public Task RemoveAsync(string key) { lock (gate) sessions.Remove(key); return Task.CompletedTask; }

    private static Entry Create(AuthenticationTicket ticket, DateTimeOffset absolute, DateTimeOffset now)
    {
        var expiry = now + IdleLifetime < absolute ? now + IdleLifetime : absolute;
        ticket.Properties.IssuedUtc = now;
        ticket.Properties.ExpiresUtc = expiry;
        return new(TicketSerializer.Default.Serialize(ticket), absolute, expiry);
    }
}
