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
    public void SqlGeneratorRejectsValuesNotProducedByFactory()
    {
        var fixture = FixtureFactory.Create()[0];
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { Username = "' OR 1=1 --" }));
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { PasswordHash = "'; DROP TABLE users;--" }));
        Assert.Throws<ArgumentException>(() => FixtureFactory.InsertSql(fixture with { SecurityStamp = "'" }));
    }
}
