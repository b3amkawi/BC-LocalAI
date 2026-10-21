using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BcAdmin;

/// <summary>
/// Input rules for accounts written by the admin script. Username and length ceilings match BE-005's login validator, so every
/// account created here can sign in. The 12-character minimum is a draft until the company password policy is approved.
/// </summary>
internal static class AccountPolicy
{
    internal const int MinPasswordCharacters = 12;
    internal const int MaxPasswordCharacters = 1024;
    internal const int MaxPasswordUtf8Bytes = 4096;
    internal static readonly string[] Roles = ["User", "Admin"];
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool IsValidUsername(string? username) =>
        !string.IsNullOrEmpty(username) && username.Length <= 128 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Same normalization as the login lookup (MySqlAuthenticationBackend).</summary>
    internal static string Normalize(string username) => username.ToUpperInvariant();

    internal static bool IsValidDisplayName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && !name.Any(char.IsControl) && IsStrictUtf8(name);

    internal static bool IsValidActor(string? actor) =>
        !string.IsNullOrWhiteSpace(actor) && actor.Length <= 128 && !actor.Any(char.IsControl) && IsStrictUtf8(actor);

    internal static bool IsValidRole(string? role) => role is not null && Roles.Contains(role, StringComparer.Ordinal);

    /// <summary>Returns a reason a password is refused, or null. The reason never contains the password.</summary>
    internal static string? CheckPassword(string password, string? username)
    {
        if (password.Length < MinPasswordCharacters) return $"Password must have at least {MinPasswordCharacters} characters.";
        if (password.Length > MaxPasswordCharacters) return $"Password must have at most {MaxPasswordCharacters} characters.";
        if (string.IsNullOrWhiteSpace(password)) return "Password must not be only spaces.";
        if (!IsStrictUtf8(password) || StrictUtf8.GetByteCount(password) > MaxPasswordUtf8Bytes) return "Password is not valid text or is too long.";
        if (username is not null && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "Password must not contain the username.";
        return null;
    }

    /// <summary>IdentityV3, HMAC-SHA512, 220,000 iterations, 16-byte salt: the only format IdentityPasswordVerifier accepts.</summary>
    internal static string HashPassword(string password) =>
        new PasswordHasher<string>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 220_000
        })).HashPassword("unused", password);

    /// <summary>64 hex characters, like the SEC-001 fixtures. A new value invalidates every session of the account.</summary>
    internal static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static bool IsStrictUtf8(string value)
    {
        try { _ = StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
