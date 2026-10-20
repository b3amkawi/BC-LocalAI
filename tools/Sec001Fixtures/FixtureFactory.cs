using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Sec001Fixtures;

// Offline fixture generator only. Not an authentication service and not wired into the application.
internal static class FixtureFactory
{
    internal const int Iterations = 220_000;

    internal static PasswordHasher<string> CreateHasher() => new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = Iterations
    }));

    internal static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static Fixture[] Create()
    {
        var hasher = CreateHasher();
        return new[] { "active", "disabled", "locked" }.Select(state =>
        {
            var password = RandomSecret();
            var username = $"sec001.{state}";
            return new Fixture(Guid.NewGuid().ToString(), username, password,
                hasher.HashPassword(username, password), RandomSecret(), state);
        }).ToArray();
    }

    internal static string InsertSql(Fixture fixture)
    {
        // Only factory-controlled values are accepted; never pass user-supplied SQL here.
        if (!Guid.TryParseExact(fixture.Id, "D", out _) ||
            fixture.State is not ("active" or "disabled" or "locked") ||
            fixture.Username != $"sec001.{fixture.State}" ||
            fixture.SecurityStamp.Length != 64 || !fixture.SecurityStamp.All(Uri.IsHexDigit) ||
            fixture.PasswordHash.Length == 0 ||
            fixture.PasswordHash.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=')))
            throw new ArgumentException("Only generated synthetic fixtures may be inserted.");

        var active = fixture.State == "disabled" ? 0 : 1;
        var lockout = fixture.State == "locked" ? "'2099-01-01 00:00:00'" : "NULL";
        return $"INSERT INTO users (id,username,normalized_username,display_name,password_hash,is_active,security_stamp,lockout_end_utc) VALUES ('{fixture.Id}','{fixture.Username}','{fixture.Username.ToUpperInvariant()}','Synthetic {fixture.State}','{fixture.PasswordHash}',{active},'{fixture.SecurityStamp}',{lockout});";
    }
}

internal sealed record Fixture(string Id, string Username, string Password, string PasswordHash,
    string SecurityStamp, string State);
