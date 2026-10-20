using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.Authentication;

public interface IAuthenticationPasswordVerifier
{
    bool Verify(string? encodedHash, string password);
}

public sealed class IdentityPasswordVerifier : IAuthenticationPasswordVerifier
{
    private readonly PasswordHasher<string> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 220_000
    }));
    private readonly string dummy;

    public IdentityPasswordVerifier() => dummy = hasher.HashPassword("dummy", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public bool Verify(string? encodedHash, string password)
    {
        // Refuse malformed, obsolete or attacker-controlled unbounded work-factor headers before framework KDF.
        // This is a format/work guard only; Microsoft performs the actual password verification/constant-time comparison.
        var supported = IsSupported(encodedHash);
        var result = hasher.VerifyHashedPassword("unused", supported ? encodedHash! : dummy, password);
        return supported && result != PasswordVerificationResult.Failed; // Never write/re-hash on login.
    }

    private static bool IsSupported(string? encodedHash)
    {
        if (encodedHash is null || encodedHash.Length > 512) return false;
        Span<byte> buffer = stackalloc byte[384];
        if (!Convert.TryFromBase64String(encodedHash, buffer, out var length) || length < 61 || buffer[0] != 1) return false;
        var prf = BinaryPrimitives.ReadUInt32BigEndian(buffer[1..]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(buffer[5..]);
        var salt = BinaryPrimitives.ReadUInt32BigEndian(buffer[9..]);
        // SEC-001 generated IdentityV3 SHA512/220k hashes only; other policies require review, not silent fallback.
        return prf == 2 && iterations == 220_000 && salt == 16 && length == 61;
    }
}
