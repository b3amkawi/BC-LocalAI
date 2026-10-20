using System.Security.Claims;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using AuthOptions = BC_LocalAI.Configuration.AuthenticationOptions;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationSessionAndLimitTests
{
    // BE-005's ticket-store tests moved to BE-006 (Tests/Security): the store was replaced by server-side sessions.

    [Fact]
    public void AccountLimitIsCaseInsensitiveAndRefillsAtTheNextWindow()
    {
        var clock = new AuthenticationServiceTests.Clock();
        using var limiter = new LoginAttemptLimiter(Options.Create(new AuthOptions { AccountAttemptsPerMinute = 1 }), clock);
        using (var first = limiter.TryAcquire("Synthetic", "ip")) Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire("SYNTHETIC", "other-ip"));
        clock.Now += TimeSpan.FromMinutes(1);
        using var next = limiter.TryAcquire("synthetic", "ip");
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IpAndGlobalBudgetsStopUsernameSpraying(bool global)
    {
        using var limiter = new LoginAttemptLimiter(Options.Create(new AuthOptions
        { IpAttemptsPerMinute = global ? 30 : 1, GlobalAttemptsPerMinute = global ? 1 : 120 }), new AuthenticationServiceTests.Clock());
        using (var first = limiter.TryAcquire("user1", "same-ip")) Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire("user2", global ? "other-ip" : "same-ip"));
    }

    [Fact]
    public void ConcurrentKdfBudgetHasNoQueueAndReleaseIsIdempotent()
    {
        using var limiter = new LoginAttemptLimiter(Options.Create(new AuthOptions { ConcurrentLogins = 1 }), new AuthenticationServiceTests.Clock());
        var first = limiter.TryAcquire("user", "ip")!;
        Assert.Null(limiter.TryAcquire("other-user", "other-ip"));
        first.Dispose(); first.Dispose();
        using var next = limiter.TryAcquire("other-user", "other-ip");
        Assert.NotNull(next);
    }
}
