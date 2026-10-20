using System.Security.Claims;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using AuthOptions = BC_LocalAI.Configuration.AuthenticationOptions;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationSessionAndLimitTests
{
    private static AuthenticationTicket Ticket() => new(new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "synthetic-id")], AuthenticationRegistration.Scheme)), new AuthenticationProperties(), AuthenticationRegistration.Scheme);

    [Fact]
    public async Task ExpiredOrRevokedTicketCannotBeReplayedOrRenewed()
    {
        var clock = new AuthenticationServiceTests.Clock();
        var store = new BasicAuthenticationTicketStore(Options.Create(new AuthOptions()), clock);
        var key = await store.StoreAsync(Ticket());
        Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        await store.RenewAsync(key, Ticket());
        Assert.Null(await store.RetrieveAsync(key));
        key = await store.StoreAsync(Ticket());
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.Null(await store.RetrieveAsync(key));
        await store.RenewAsync(key, Ticket());
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task SlidingRenewalCannotExtendTheEightHourAbsoluteCeiling()
    {
        var clock = new AuthenticationServiceTests.Clock();
        var store = new BasicAuthenticationTicketStore(Options.Create(new AuthOptions()), clock);
        var key = await store.StoreAsync(Ticket());
        for (var minute = 20; minute <= 460; minute += 20)
        {
            clock.Now += TimeSpan.FromMinutes(20);
            await store.RenewAsync(key, Ticket());
            Assert.NotNull(await store.RetrieveAsync(key));
        }
        clock.Now += TimeSpan.FromMinutes(20);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task SessionCapacityFailsClosedAndExpiredSlotsAreReclaimed()
    {
        var clock = new AuthenticationServiceTests.Clock();
        var store = new BasicAuthenticationTicketStore(Options.Create(new AuthOptions { MaxSessions = 1 }), clock);
        await store.StoreAsync(Ticket());
        await Assert.ThrowsAsync<AuthenticationDependencyException>(() => store.StoreAsync(Ticket()));
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.NotNull(await store.StoreAsync(Ticket()));
    }

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
