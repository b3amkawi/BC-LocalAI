using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using Microsoft.Extensions.Logging.Abstractions;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationServiceTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Repository(AuthenticationAccount? account) : IAuthenticationRepository
    {
        public string? LookedUp { get; private set; }
        public Task<AuthenticationAccount?> FindAsync(string normalizedUsername, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); LookedUp = normalizedUsername; return Task.FromResult(account); }
    }

    private sealed class Verifier(bool valid) : IAuthenticationPasswordVerifier
    {
        public int Calls { get; private set; }
        public string? Password { get; private set; }
        public bool Verify(string? hash, string password) { Calls++; Password = password; return valid; }
    }

    private sealed class Attempts(bool allowed = true, bool unavailable = false) : IAuthenticationAttemptStore
    {
        public int Calls { get; private set; }
        public string? UserId { get; private set; }
        public bool Verified { get; private set; }
        public Task<bool> RecordAsync(string? userId, string? stamp, bool verified, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; UserId = userId; Verified = verified;
            if (unavailable) throw new AuthenticationDependencyException();
            return Task.FromResult(allowed && userId is not null);
        }
    }

    [Theory]
    [InlineData("active", true, AuthenticationOutcome.Success)]
    [InlineData("active", false, AuthenticationOutcome.InvalidCredentials)]
    [InlineData("unknown", false, AuthenticationOutcome.InvalidCredentials)]
    [InlineData("disabled", true, AuthenticationOutcome.InvalidCredentials)]
    [InlineData("locked", true, AuthenticationOutcome.InvalidCredentials)]
    [InlineData("bad-role", true, AuthenticationOutcome.InvalidCredentials)]
    [InlineData("bad-id", true, AuthenticationOutcome.InvalidCredentials)]
    public async Task LookupStatusAndPasswordAreCheckedWithoutSkippingTheKdf(string state, bool valid, AuthenticationOutcome expected)
    {
        var repository = new Repository(state == "unknown" ? null : new AuthenticationAccount
        {
            Id = state == "bad-id" ? "not-a-uuid" : Guid.NewGuid().ToString(), Username = "Synthetic.User", DisplayName = "สมมติ",
            PasswordHash = "synthetic-hash", IsActive = state != "disabled", Role = state == "bad-role" ? "forged" : "User",
            LockoutEndUtc = state == "locked" ? DateTimeOffset.MaxValue : null, SecurityStamp = new string('A', 64)
        });
        var verifier = new Verifier(valid);
        var attempts = new Attempts();
        var backend = new MySqlAuthenticationBackend(repository, verifier, new Clock(), NullLogger<MySqlAuthenticationBackend>.Instance, attempts);
        var result = await backend.AuthenticateAsync(new LoginRequest { Username = "Synthetic.User", Password = " รหัส😀\n " });
        Assert.Equal(expected, result.Outcome);
        Assert.Equal("SYNTHETIC.USER", repository.LookedUp);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(1, attempts.Calls);
        Assert.Equal(state == "active", attempts.UserId is not null);
        Assert.Equal(state == "active" && valid, attempts.Verified);
        Assert.Equal(" รหัส😀\n ", verifier.Password);
        if (expected != AuthenticationOutcome.Success) Assert.Null(result.User);
        else Assert.Equal(["User"], result.User!.Roles);
    }

    [Fact]
    public async Task CancellationIsNotReclassifiedAsInvalidCredentials()
    {
        var backend = new MySqlAuthenticationBackend(new Repository(null), new Verifier(false), new Clock(), NullLogger<MySqlAuthenticationBackend>.Instance, new Attempts());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.AuthenticateAsync(new LoginRequest(), new CancellationToken(true)));
    }

    [Fact]
    public async Task DependencyFailureIsSafeUnavailableAndNeverFallsBackToMockAuthentication()
    {
        var verifier = new Verifier(true);
        var backend = new MySqlAuthenticationBackend(new UnavailableRepository(), verifier, new Clock(), NullLogger<MySqlAuthenticationBackend>.Instance, new Attempts());
        var result = await backend.AuthenticateAsync(new LoginRequest { Username = "synthetic", Password = "synthetic" });
        Assert.Equal(AuthenticationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.User);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(false, AuthenticationOutcome.InvalidCredentials)]
    [InlineData(true, AuthenticationOutcome.Unavailable)]
    public async Task StaleSuccessOrWriterFailureCannotIssueAuthentication(bool unavailable, AuthenticationOutcome expected)
    {
        var repository = new Repository(new AuthenticationAccount
        {
            Id = Guid.NewGuid().ToString(), Username = "synthetic", DisplayName = "synthetic", PasswordHash = "synthetic",
            IsActive = true, Role = "User", SecurityStamp = new string('A', 64)
        });
        var backend = new MySqlAuthenticationBackend(repository, new Verifier(true), new Clock(),
            NullLogger<MySqlAuthenticationBackend>.Instance, new Attempts(allowed: false, unavailable: unavailable));
        var result = await backend.AuthenticateAsync(new LoginRequest { Username = "synthetic", Password = "synthetic" });
        Assert.Equal(expected, result.Outcome); Assert.Null(result.User);
    }

    private sealed class UnavailableRepository : IAuthenticationRepository
    {
        public Task<AuthenticationAccount?> FindAsync(string normalizedUsername, CancellationToken cancellationToken) =>
            throw new AuthenticationDependencyException();
    }

    [Theory]
    [InlineData(" รหัสผ่านภาษาไทย😀\n ")]
    [InlineData("e\u0301")]
    [InlineData("é")]
    [InlineData("   ")]
    public void ReviewedHasherVerifiesExactPasswordAndRejectsModifiedValue(string password)
    {
        var hasher = Sec001Fixtures.FixtureFactory.CreateHasher();
        var hash = hasher.HashPassword("unused", password);
        var verifier = new IdentityPasswordVerifier();
        Assert.True(verifier.Verify(hash, password));
        Assert.False(verifier.Verify(hash, password + "wrong"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public void UnknownAndMalformedHashesFailSafelyWithDummyVerification(string? hash) =>
        Assert.False(new IdentityPasswordVerifier().Verify(hash, "synthetic"));

    [Fact]
    public void UnsupportedOrUnboundedWorkFactorsAreRejectedBeforeKdf()
    {
        var hash = Sec001Fixtures.FixtureFactory.CreateHasher().HashPassword("unused", "synthetic");
        var bytes = Convert.FromBase64String(hash);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), uint.MaxValue);
        Assert.False(new IdentityPasswordVerifier().Verify(Convert.ToBase64String(bytes), "synthetic"));
    }
}
