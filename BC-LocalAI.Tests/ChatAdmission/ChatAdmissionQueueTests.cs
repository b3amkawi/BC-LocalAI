using System.Net;
using System.Security.Claims;
using BC_LocalAI.Configuration;
using BC_LocalAI.Services;
using BC_LocalAI.Services.ChatAdmission;

namespace BC_LocalAI.Tests.ChatAdmission;

/// <summary>BE-008 unit tests of the gate itself (no HTTP, no Ollama). Expected results were written before the code ran.</summary>
public sealed class ChatAdmissionQueueTests
{
    private static ChatAdmissionQueue Gate(int concurrent = 1, int queue = 3, int wait = 30, int perUser = 16, int retry = 7) =>
        Admissions.Create(new ChatQueueOptions
        {
            MaxConcurrentRequests = concurrent,
            MaxQueueLength = queue,
            MaxQueueWaitSeconds = wait,
            MaxPendingPerUser = perUser,
            RetryAfterSeconds = retry
        });

    private static async Task<bool> Granted(ChatTicket ticket, int millisecondsToWait = 2000)
    {
        var wait = ticket.WaitAsync(CancellationToken.None);
        return await Task.WhenAny(wait, Task.Delay(millisecondsToWait)) == wait && wait.IsCompletedSuccessfully;
    }

    [Fact]
    public void FreeSlots_AreGrantedAtOnceUpToTheLimit()
    {
        var gate = Gate(concurrent: 2);

        using var a = gate.Admit("a");
        using var b = gate.Admit("b");
        using var c = gate.Admit("c");

        Assert.False(a.Queued);
        Assert.False(b.Queued);
        Assert.True(c.Queued);
        Assert.Equal(0, c.Ahead); // two are served, nobody waits in front of c
        Assert.Equal((2, 1), gate.Snapshot());
    }

    [Fact]
    public async Task Waiters_AreServedInArrivalOrder()
    {
        var gate = Gate(concurrent: 1, queue: 5);
        var first = gate.Admit("u1");
        var tickets = Enumerable.Range(2, 4).Select(i => gate.Admit($"u{i}")).ToList();
        var served = new List<int>();

        var waits = tickets.Select((ticket, index) => Task.Run(async () =>
        {
            await ticket.WaitAsync(CancellationToken.None);
            lock (served) served.Add(index);
            ticket.Dispose();
        })).ToList();

        Assert.Equal([0, 1, 2, 3], tickets.Select(t => t.Ahead));
        first.Dispose();
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 1, 2, 3], served);
        Assert.Equal((0, 0), gate.Snapshot());
    }

    [Fact]
    public void FullQueue_IsRefusedAtOnceWithBusyAndRetryAfter()
    {
        var gate = Gate(concurrent: 1, queue: 2, retry: 7);
        using var active = gate.Admit("a");
        using var w1 = gate.Admit("b");
        using var w2 = gate.Admit("c");

        var refused = Assert.Throws<AiServiceException>(() => gate.Admit("d"));

        Assert.Equal("AI_BUSY", refused.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(7, refused.RetryAfterSeconds);
        Assert.Equal((1, 2), gate.Snapshot());
        Assert.Equal(1, gate.Metrics.RejectedQueueFullTotal);
    }

    [Fact]
    public void QueueLengthZero_RefusesEveryoneWhoWouldHaveToWait()
    {
        var gate = Gate(concurrent: 1, queue: 0);
        using var active = gate.Admit("a");

        Assert.Equal("AI_BUSY", Assert.Throws<AiServiceException>(() => gate.Admit("b")).Code);
    }

    [Fact]
    public void UserOverQuota_IsRefusedWith429_OtherUsersAreNot()
    {
        var gate = Gate(concurrent: 1, queue: 5, perUser: 1);
        using var mine = gate.Admit("alice");

        var refused = Assert.Throws<AiServiceException>(() => gate.Admit("alice"));
        using var other = gate.Admit("bob");

        Assert.Equal("AI_REQUEST_IN_PROGRESS", refused.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(7, refused.RetryAfterSeconds);
        Assert.True(other.Queued);
        Assert.Equal(1, gate.Metrics.RejectedUserQuotaTotal);
    }

    [Fact]
    public void UserQuotaIsCheckedBeforeQueueFull()
    {
        var gate = Gate(concurrent: 1, queue: 0, perUser: 1);
        using var mine = gate.Admit("alice");

        // The queue is full too, but the user is told about their own request first (429, not 503).
        Assert.Equal(HttpStatusCode.TooManyRequests, Assert.Throws<AiServiceException>(() => gate.Admit("alice")).StatusCode);
    }

    [Fact]
    public void QuotaCountsWaitingRequestsToo_AndIsFreedByDispose()
    {
        var gate = Gate(concurrent: 1, queue: 5, perUser: 2);
        using var blocker = gate.Admit("other");
        var q1 = gate.Admit("alice");
        var q2 = gate.Admit("alice");
        Assert.Throws<AiServiceException>(() => gate.Admit("alice"));

        q1.Dispose();
        using var again = gate.Admit("alice"); // one place came back

        q2.Dispose();
    }

    [Fact]
    public async Task CancelledWhileWaiting_LeavesTheQueue_AndIsNeverGranted()
    {
        var gate = Gate(concurrent: 1, queue: 3, perUser: 1);
        var active = gate.Admit("a");
        var waiting = gate.Admit("b");
        using var cts = new CancellationTokenSource();
        var wait = waiting.WaitAsync(cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        Assert.Equal((1, 0), gate.Snapshot()); // it no longer takes a place in line
        Assert.Equal(1, gate.Metrics.CancelledTotal);
        var again = gate.Admit("b"); // its quota came back
        active.Dispose();

        // The cancelled request never got the slot; the new one did.
        Assert.True(await Granted(again));
        Assert.Equal((1, 0), gate.Snapshot());
        again.Dispose();
        Assert.Equal((0, 0), gate.Snapshot());
    }

    [Fact]
    public async Task CancelledInTheMiddle_DoesNotBlockTheOnesBehindIt()
    {
        var gate = Gate(concurrent: 1, queue: 5);
        var active = gate.Admit("a");
        var middle = gate.Admit("b");
        var last = gate.Admit("c");
        using var cts = new CancellationTokenSource();
        var middleWait = middle.WaitAsync(cts.Token);
        var lastWait = last.WaitAsync(CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleWait);
        active.Dispose();

        await lastWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, 0), gate.Snapshot());
        last.Dispose();
    }

    [Fact]
    public async Task WaitingTooLong_IsBusy_NotTimeout_AndReleasesTheTicket()
    {
        var gate = Gate(concurrent: 1, queue: 3, wait: 1);
        using var active = gate.Admit("a");
        var waiting = gate.Admit("b");

        var error = await Assert.ThrowsAsync<AiServiceException>(() => waiting.WaitAsync(CancellationToken.None));

        Assert.Equal("AI_BUSY", error.Code);
        Assert.Equal("queue-wait-timeout", error.Reason);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(7, error.RetryAfterSeconds);
        Assert.Equal((1, 0), gate.Snapshot());
        Assert.Equal(1, gate.Metrics.WaitTimedOutTotal);
    }

    [Fact]
    public async Task DisposeWhileWaiting_RemovesTheRequestFromTheQueue()
    {
        var gate = Gate(concurrent: 1, queue: 3);
        var active = gate.Admit("a");
        var waiting = gate.Admit("b");
        var next = gate.Admit("c");

        waiting.Dispose();
        active.Dispose();

        Assert.True(await Granted(next));
        Assert.Equal((1, 0), gate.Snapshot());
        next.Dispose();
    }

    [Fact]
    public void DisposeTwice_ReleasesOnce()
    {
        var gate = Gate(concurrent: 1, queue: 3);
        var a = gate.Admit("a");
        var b = gate.Admit("b");

        a.Dispose();
        a.Dispose();

        Assert.Equal((1, 0), gate.Snapshot()); // b holds the slot; the second Dispose did not hand out another
        b.Dispose();
        Assert.Equal((0, 0), gate.Snapshot());
    }

    [Fact]
    public async Task HeavyConcurrency_NeverExceedsTheLimit_AndLeaksNothing()
    {
        const int limit = 3;
        var gate = Gate(concurrent: limit, queue: 500, wait: 60);
        var running = 0;
        var maxRunning = 0;
        var served = 0;
        var random = new Random(20261021);
        var cancelAfterMs = Enumerable.Range(0, 300).Select(_ => random.Next(0, 40)).ToArray();

        var jobs = Enumerable.Range(0, 300).Select(i => Task.Run(async () =>
        {
            using var ticket = gate.Admit($"user-{i}");
            using var cts = new CancellationTokenSource();
            if (i % 3 == 0) cts.CancelAfter(cancelAfterMs[i]);
            try
            {
                await ticket.WaitAsync(cts.Token);
                var now = Interlocked.Increment(ref running);
                int seen;
                while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
                await Task.Delay(3);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref served);
            }
            catch (OperationCanceledException)
            {
            }
        })).ToList();

        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(maxRunning <= limit, $"{maxRunning} requests ran at once, limit {limit}");
        Assert.True(served >= 200, $"only {served} of 300 were served");
        Assert.Equal((0, 0), gate.Snapshot());
        // Nothing leaked: a fresh round of `limit` requests from the same users is admitted at once.
        var fresh = Enumerable.Range(0, limit).Select(i => gate.Admit($"user-{i}")).ToList();
        Assert.All(fresh, t => Assert.False(t.Queued));
        fresh.ForEach(t => t.Dispose());
    }

    [Fact]
    public async Task GrantRacingWithCancellation_NeverLeaksTheSlot()
    {
        var gate = Gate(concurrent: 1, queue: 10, wait: 30);
        for (var round = 0; round < 200; round++)
        {
            var holder = gate.Admit("holder");
            var waiting = gate.Admit($"w{round}");
            using var cts = new CancellationTokenSource();
            var wait = waiting.WaitAsync(cts.Token);

            var release = Task.Run(holder.Dispose);
            var cancel = Task.Run(cts.Cancel);
            await Task.WhenAll(release, cancel);
            try { await wait; } catch (OperationCanceledException) { }
            waiting.Dispose();
        }

        Assert.Equal((0, 0), gate.Snapshot());
        using var finalTicket = gate.Admit("final");
        Assert.False(finalTicket.Queued);
    }

    [Fact]
    public void Metrics_CountAdmissions()
    {
        var gate = Gate(concurrent: 1, queue: 1);
        using var a = gate.Admit("a");

        Assert.Equal(1, gate.Metrics.AdmittedTotal);
    }

    [Fact]
    public void UserKey_UsesTheAuthenticatedIdentityOnly()
    {
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim("userId", "someone-else")], "test"));
        var claimWithoutAuthentication = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u-2")]));

        Assert.Equal("u-1", ChatAdmissionQueue.UserKey(signedIn));
        // With no authenticated identity (Authentication:Enabled=false) everybody shares one key: there is no way around the quota.
        Assert.Equal(ChatAdmissionQueue.AnonymousUserKey, ChatAdmissionQueue.UserKey(claimWithoutAuthentication));
        Assert.Equal(ChatAdmissionQueue.AnonymousUserKey, ChatAdmissionQueue.UserKey(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

public sealed class ChatQueueOptionsValidatorTests
{
    private static readonly ChatQueueOptionsValidator Validator = new();

    [Fact]
    public void Defaults_AreValid() => Assert.True(Validator.Validate(null, new ChatQueueOptions()).Succeeded);

    [Theory]
    [InlineData(nameof(ChatQueueOptions.MaxConcurrentRequests), 0)]
    [InlineData(nameof(ChatQueueOptions.MaxConcurrentRequests), 17)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueLength), -1)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueLength), 257)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueWaitSeconds), 0)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueWaitSeconds), 301)]
    [InlineData(nameof(ChatQueueOptions.MaxPendingPerUser), 0)]
    [InlineData(nameof(ChatQueueOptions.MaxPendingPerUser), 17)]
    [InlineData(nameof(ChatQueueOptions.RetryAfterSeconds), 0)]
    [InlineData(nameof(ChatQueueOptions.RetryAfterSeconds), 301)]
    public void OutOfRangeValue_IsRejectedAndNamed(string property, int value)
    {
        var options = new ChatQueueOptions();
        typeof(ChatQueueOptions).GetProperty(property)!.SetValue(options, value);

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(property, result.FailureMessage);
    }

    [Theory]
    [InlineData(nameof(ChatQueueOptions.MaxConcurrentRequests), 1)]
    [InlineData(nameof(ChatQueueOptions.MaxConcurrentRequests), 16)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueLength), 0)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueLength), 256)]
    [InlineData(nameof(ChatQueueOptions.MaxQueueWaitSeconds), 300)]
    public void BoundaryValues_AreAccepted(string property, int value)
    {
        var options = new ChatQueueOptions();
        typeof(ChatQueueOptions).GetProperty(property)!.SetValue(options, value);

        Assert.True(Validator.Validate(null, options).Succeeded);
    }
}
