using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BC_LocalAI.Configuration;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.Authentication;

/// <summary>Single-process bounded attempt/concurrency guard. No raw usernames/IPs retained; no attacker-grown partitions.</summary>
public sealed class LoginAttemptLimiter(IOptions<AuthenticationOptions> options, TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly Bucket[] accounts = new Bucket[4096];
    private readonly Bucket[] addresses = new Bucket[1024];
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim concurrency = new(options.Value.ConcurrentLogins);
    private Bucket global;
    private struct Bucket { public long Window; public int Count; }

    public IDisposable? TryAcquire(string username, string address)
    {
        var account = Slot(username.ToUpperInvariant(), accounts.Length);
        var ip = Slot(address, addresses.Length);
        var window = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (gate)
        {
            if (!Consume(ref global, window, options.Value.GlobalAttemptsPerMinute) ||
                !Consume(ref addresses[ip], window, options.Value.IpAttemptsPerMinute) ||
                !Consume(ref accounts[account], window, options.Value.AccountAttemptsPerMinute) || !concurrency.Wait(0)) return null;
        }
        return new Lease(concurrency);
    }

    private int Slot(string value, int length) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value))) % (uint)length);

    private static bool Consume(ref Bucket bucket, long window, int limit)
    {
        if (bucket.Window != window) bucket = new Bucket { Window = window };
        if (bucket.Count >= limit) return false;
        bucket.Count++;
        return true;
    }

    public void Dispose() { concurrency.Dispose(); CryptographicOperations.ZeroMemory(key); }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) semaphore.Release(); }
    }
}
