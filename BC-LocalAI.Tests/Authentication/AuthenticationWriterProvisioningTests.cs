using Sec001Fixtures;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationWriterProvisioningTests
{
    [Fact]
    public void WriterReceivesOnlyColumnScopedAttemptPermissionsAndRequiresTls()
    {
        var sql = AttemptWriterBootstrap.Create(new string('A', 64));
        Assert.Equal(3, sql.Length);
        Assert.Contains("REQUIRE SSL", sql[0]);
        Assert.Equal("GRANT SELECT (id, is_active, security_stamp, access_failed_count, lockout_end_utc) ON bc_localai_sec001.users TO 'bc_auth_attempt_writer'@'127.0.0.1';", sql[1]);
        Assert.Equal("GRANT UPDATE (access_failed_count, lockout_end_utc, updated_at_utc) ON bc_localai_sec001.users TO 'bc_auth_attempt_writer'@'127.0.0.1';", sql[2]);
        var grants = string.Join('\n', sql.Skip(1));
        Assert.DoesNotContain("password_hash", grants); Assert.DoesNotContain("role", grants);
        Assert.DoesNotContain("ON *.*", grants, StringComparison.Ordinal); Assert.DoesNotContain("ALL PRIVILEGES", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH GRANT OPTION", grants);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-generated")]
    [InlineData("x'; GRANT ALL ON *.*; --")]
    public void BootstrapRejectsUncontrolledSecrets(string secret) =>
        Assert.Throws<ArgumentException>(() => AttemptWriterBootstrap.Create(secret));
}
