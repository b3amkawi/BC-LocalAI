using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Sec001Fixtures;

namespace BC_LocalAI.Tests;

public sealed class Sec001FixtureTests
{
    [Fact]
    public void FixturesHaveUniqueSecretsIdsAndSaltsAndNoAdmin()
    {
        var fixtures = FixtureFactory.Create();
        Assert.Equal(3, fixtures.Length);
        Assert.Equal(3, fixtures.Select(f => f.Id).Distinct().Count());
        Assert.Equal(3, fixtures.Select(f => f.Password).Distinct().Count());
        Assert.Equal(3, fixtures.Select(f => f.PasswordHash).Distinct().Count());
        Assert.Equal(["active", "disabled", "locked"], fixtures.Select(f => f.State));
        foreach (var fixture in fixtures)
        {
            Assert.Equal(64, fixture.Password.Length);
            var hash = Convert.FromBase64String(fixture.PasswordHash);
            Assert.Equal(1, hash[0]); // IdentityV3 version marker, not a custom format.
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1, 4))); // HMAC-SHA512
            Assert.Equal((uint)FixtureFactory.Iterations, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5, 4)));
            Assert.True(BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9, 4)) >= 16);
            Assert.DoesNotContain(fixture.Password, fixture.PasswordHash);
            // No Admin: the insert never sets a role, so the column default 'User' applies.
            var sql = FixtureFactory.InsertSql(fixture);
            Assert.DoesNotContain("Admin", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("รหัสผ่านสมมติ😀\n A")]
    [InlineData("Leading and trailing spaces ")]
    public void StandardHasherVerifiesExactUnicodePasswordAndRejectsDifferentPassword(string password)
    {
        var hasher = FixtureFactory.CreateHasher();
        var hash = hasher.HashPassword("synthetic", password);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword("synthetic", hash, password));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword("synthetic", hash, password + "wrong"));
        Assert.NotEqual(hash, hasher.HashPassword("synthetic", password));
    }

    [Fact]
    public void GeneratedSqlOmitsPlaintextAndPreservesAccountStates()
    {
        foreach (var fixture in FixtureFactory.Create())
        {
            var sql = FixtureFactory.InsertSql(fixture);
            Assert.DoesNotContain(fixture.Password, sql);
            Assert.Contains(fixture.PasswordHash, sql);
            Assert.Contains(fixture.Username.ToUpperInvariant(), sql);
            Assert.Equal(fixture.State == "locked", sql.Contains("2099-01-01"));
        }
    }

    [Fact]
    public void Bootstrap_GivesEveryDatabaseAccountItsOwnSecret_AndTheReaderOnlySelect()
    {
        var secrets = DatabaseSecrets.Create();
        var fixtures = FixtureFactory.Create();
        var script = string.Join("\n", BootstrapScript.Create(secrets, "CREATE TABLE users (id INT);", fixtures));

        Assert.Equal(3, new[] { secrets.Root, secrets.Admin, secrets.Reader }.Distinct().Count());
        Assert.Single(script.Split('\n'), line => line.Contains(secrets.Root));
        Assert.Contains($"CREATE USER 'bc_sec001_admin'@'127.0.0.1' IDENTIFIED BY '{secrets.Admin}';", script);
        Assert.Contains($"CREATE USER 'bc_auth_reader'@'127.0.0.1' IDENTIFIED BY '{secrets.Reader}';", script);
        Assert.Contains("GRANT SELECT ON bc_localai_sec001.users TO 'bc_auth_reader'@'127.0.0.1';", script);
        Assert.Single(script.Split('\n'), line => line.Contains("TO 'bc_auth_reader'"));
        Check.NotLeaked(script, "ON *.* TO 'bc_auth_reader'", "GRANT ALL PRIVILEGES ON *.*");
        // User passwords never reach the database or the script, only their hashes.
        Check.NotLeaked(script, fixtures.Select(f => f.Password).ToArray());
    }

    [Fact]
    public void Bootstrap_RefusesASharedOrForeignSecret()
    {
        var secrets = DatabaseSecrets.Create();
        var fixtures = FixtureFactory.Create();

        Assert.Throws<ArgumentException>(() => BootstrapScript.Create(secrets with { Admin = secrets.Root }, "", fixtures));
        Assert.Throws<ArgumentException>(() => BootstrapScript.Create(secrets with { Reader = "x'; DROP USER root; --" }, "", fixtures));
    }

    [Theory]
    [InlineData(3307)]
    [InlineData(3317)]
    public void OptionFile_UsesLoopbackTcpAndTheGivenPort(int port)
    {
        var file = BootstrapScript.OptionFile("bc_auth_reader", "secret", port);

        Assert.Contains("host=127.0.0.1\n", file);
        Assert.Contains($"port={port}\n", file);
        Assert.Contains("protocol=TCP\n", file);
    }

    [Fact]
    public void SqlGeneratorRejectsValuesNotProducedByFactory()
    {
        var fixture = FixtureFactory.Create()[0];
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { Username = "' OR 1=1 --" }));
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { PasswordHash = "'; DROP TABLE users;--" }));
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { SecurityStamp = "'" }));
    }
}
