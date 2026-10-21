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

/// <summary>One secret per database account: knowing one never gives another.</summary>
internal sealed record DatabaseSecrets(string Root, string Admin, string Reader)
{
    internal static DatabaseSecrets Create() =>
        new(FixtureFactory.RandomSecret(), FixtureFactory.RandomSecret(), FixtureFactory.RandomSecret());
}

internal static class BootstrapScript
{
    /// <summary>
    /// SQL run once by mysqld --init-file. Root, admin and reader get different random secrets;
    /// the admin account is limited to the SEC-001 schema (plus SHUTDOWN for Stop) and the reader may only SELECT users.
    /// </summary>
    internal static List<string> Create(DatabaseSecrets secrets, string schemaSql, IEnumerable<Fixture> fixtures)
    {
        foreach (var secret in new[] { secrets.Root, secrets.Admin, secrets.Reader })
        {
            if (secret.Length != 64 || !secret.All(Uri.IsHexDigit))
                throw new ArgumentException("Only generated secrets may be used.");
        }

        if (new[] { secrets.Root, secrets.Admin, secrets.Reader }.Distinct().Count() != 3)
            throw new ArgumentException("Each database account needs its own secret.");

        var script = new List<string>
        {
            $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{secrets.Root}';",
            schemaSql
        };
        script.AddRange(fixtures.Select(FixtureFactory.InsertSql));
        script.Add($"CREATE USER 'bc_sec001_admin'@'127.0.0.1' IDENTIFIED BY '{secrets.Admin}';");
        script.Add("GRANT ALL PRIVILEGES ON bc_localai_sec001.* TO 'bc_sec001_admin'@'127.0.0.1';");
        script.Add("GRANT SHUTDOWN ON *.* TO 'bc_sec001_admin'@'127.0.0.1';");
        script.Add($"CREATE USER 'bc_auth_reader'@'127.0.0.1' IDENTIFIED BY '{secrets.Reader}';");
        script.Add("GRANT SELECT ON bc_localai_sec001.users TO 'bc_auth_reader'@'127.0.0.1';");
        return script;
    }

    /// <summary>MySQL client option file for one account (kept in the ACL-protected private folder).</summary>
    internal static string OptionFile(string user, string password, int port) =>
        $"[client]\nuser={user}\npassword={password}\nhost=127.0.0.1\nport={port}\nprotocol=TCP\ndefault-character-set=utf8mb4\n";
}

// Opt-in BE-005 fixture only. No grants on hashes, role, status, stamp, username, table DDL or account administration.
internal static class AttemptWriterBootstrap
{
    internal static string[] Create(string secret)
    {
        if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new ArgumentException("Only generated secrets may be used.");
        return
        [
            $"CREATE USER 'bc_auth_attempt_writer'@'127.0.0.1' IDENTIFIED BY '{secret}' REQUIRE SSL;",
            "GRANT SELECT (id, is_active, security_stamp, access_failed_count, lockout_end_utc) ON bc_localai_sec001.users TO 'bc_auth_attempt_writer'@'127.0.0.1';",
            "GRANT UPDATE (access_failed_count, lockout_end_utc, updated_at_utc) ON bc_localai_sec001.users TO 'bc_auth_attempt_writer'@'127.0.0.1';"
        ];
    }
}

// Opt-in BE-007 fixture only: the chat history runtime account. Table-level SELECT/INSERT/UPDATE on the two history tables;
// nothing on users, no DELETE (deletion is a soft delete), no DDL. The tables are created here with the BE-007 migration so the
// grants have something to apply to; MySQL keeps table grants by name, so a rehearsed rollback and re-apply keeps them.
internal static class ChatHistoryBootstrap
{
    internal const string User = "bc_history_rw";

    internal static string[] Create(string secret, string migrationSql)
    {
        if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new ArgumentException("Only generated secrets may be used.");
        return
        [
            "USE bc_localai_sec001;",
            migrationSql,
            $"CREATE USER '{User}'@'127.0.0.1' IDENTIFIED BY '{secret}' REQUIRE SSL;",
            $"GRANT SELECT, INSERT, UPDATE ON bc_localai_sec001.Conversations TO '{User}'@'127.0.0.1';",
            $"GRANT SELECT, INSERT, UPDATE ON bc_localai_sec001.Messages TO '{User}'@'127.0.0.1';"
        ];
    }
}
