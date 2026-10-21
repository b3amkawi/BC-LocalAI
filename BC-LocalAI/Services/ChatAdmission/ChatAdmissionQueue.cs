using System.Security.Claims;
using BC_LocalAI.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.ChatAdmission;

/// <summary>
/// BE-008: the one gate in front of Ollama, shared by /api/chat and /api/chat/stream. At most
/// <see cref="ChatQueueOptions.MaxConcurrentRequests"/> requests are served at once; the rest wait in a bounded FIFO queue.
/// Admission is decided synchronously (<see cref="Admit"/>) so a refusal can still be a plain HTTP status; waiting is a
/// separate step (<see cref="ChatTicket.WaitAsync"/>) so a stream can open first. A request that is cancelled, times out or is
/// disposed while waiting leaves the queue at once and never reaches Ollama. Every path that ends a request releases its slot
/// and its per-user quota through <see cref="ChatTicket.Dispose"/>.
/// </summary>
public sealed class ChatAdmissionQueue
{
    /// <summary>
    /// With Authentication:Enabled=false there is no identity: every caller shares this one key, so the per-user quota then
    /// applies to everybody together (stricter, never a way around it). A client-supplied user id is never read.
    /// </summary>
    public const string AnonymousUserKey = "anonymous";

    private readonly object _gate = new();
    private readonly LinkedList<ChatTicket> _waiting = new();
    private readonly Dictionary<string, int> _pendingPerUser = new(StringComparer.Ordinal);
    private readonly ChatQueueOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatAdmissionQueue> _logger;
    private int _active;

    public ChatAdmissionQueue(IOptions<ChatQueueOptions> options, TimeProvider clock, ILogger<ChatAdmissionQueue> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        Metrics = new ChatQueueMetrics(this);
    }

    public ChatQueueMetrics Metrics { get; }

    public ChatQueueOptions Options => _options;

    /// <summary>Identity the quota is counted against: the authenticated user id, else the shared anonymous key.</summary>
    public static string UserKey(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? id
            : AnonymousUserKey;

    /// <summary>Current numbers, taken under the lock (a snapshot: it can be out of date a moment later).</summary>
    public (int Active, int Queued) Snapshot()
    {
        lock (_gate)
        {
            return (_active, _waiting.Count);
        }
    }

    /// <summary>
    /// Decides at once. Throws <see cref="AiServiceException"/> (429 when the user is over quota, else 503 when the queue is
    /// full); otherwise returns a ticket that already holds a slot or waits in line. The caller must dispose the ticket.
    /// </summary>
    public ChatTicket Admit(string userKey)
    {
        ChatTicket ticket;
        lock (_gate)
        {
            var pending = _pendingPerUser.GetValueOrDefault(userKey);
            if (pending >= _options.MaxPendingPerUser)
            {
                Metrics.RejectedUserQuota();
                ChatQueueLog.Rejected(_logger, "user-quota", _active, _waiting.Count);
                throw AiServiceException.RequestInProgress(_options.RetryAfterSeconds);
            }

            // FIFO: a new request may skip the line only when nobody is waiting (then a slot is free by invariant).
            var immediate = _waiting.Count == 0 && _active < _options.MaxConcurrentRequests;
            if (!immediate && _waiting.Count >= _options.MaxQueueLength)
            {
                Metrics.RejectedQueueFull();
                ChatQueueLog.Rejected(_logger, "queue-full", _active, _waiting.Count);
                throw AiServiceException.Busy(_options.RetryAfterSeconds, "queue-full");
            }

            ticket = new ChatTicket(this, userKey, _clock.GetTimestamp());
            _pendingPerUser[userKey] = pending + 1;
            if (immediate)
            {
                _active++;
                ticket.State = TicketState.Active;
                ticket.Grant();
                Metrics.Admitted(TimeSpan.Zero);
            }
            else
            {
                ticket.Queued = true;
                ticket.Ahead = _waiting.Count;
                ticket.Node = _waiting.AddLast(ticket);
            }
        }

        return ticket;
    }

    /// <summary>Gives free slots to the oldest waiters. Returns the tickets to complete, which is done outside the lock.</summary>
    private List<(ChatTicket Ticket, TimeSpan Waited)> PumpLocked()
    {
        var granted = new List<(ChatTicket, TimeSpan)>();
        while (_active < _options.MaxConcurrentRequests && _waiting.First is { } node)
        {
            _waiting.RemoveFirst();
            var ticket = node.Value;
            ticket.Node = null;
            _active++;
            ticket.State = TicketState.Active;
            var waited = _clock.GetElapsedTime(ticket.EnqueuedAt);
            granted.Add((ticket, waited));
        }

        return granted;
    }

    private void Complete(List<(ChatTicket Ticket, TimeSpan Waited)> granted)
    {
        foreach (var (ticket, waited) in granted)
        {
            Metrics.Admitted(waited);
            if (waited.TotalSeconds >= 5) ChatQueueLog.LongWait(_logger, (long)waited.TotalMilliseconds);
            ticket.Grant();
        }
    }

    internal void Release(ChatTicket ticket, ReleaseReason reason)
    {
        List<(ChatTicket, TimeSpan)> granted;
        lock (_gate)
        {
            if (ticket.State == TicketState.Released) return;

            if (ticket.State == TicketState.Active) _active--;
            else if (ticket.Node is { } node) _waiting.Remove(node);
            ticket.Node = null;
            ticket.State = TicketState.Released;

            if (_pendingPerUser.TryGetValue(ticket.UserKey, out var pending))
            {
                if (pending <= 1) _pendingPerUser.Remove(ticket.UserKey);
                else _pendingPerUser[ticket.UserKey] = pending - 1;
            }

            granted = PumpLocked();
        }

        switch (reason)
        {
            case ReleaseReason.Cancelled: Metrics.Cancelled(); break;
            case ReleaseReason.TimedOut: Metrics.WaitTimedOut(); break;
        }

        Complete(granted);
    }

    internal TimeProvider Clock => _clock;
}

internal enum TicketState { Waiting, Active, Released }

internal enum ReleaseReason { Finished, Cancelled, TimedOut }

/// <summary>One request's place at the gate. Disposing it (any number of times) gives back its slot and quota.</summary>
public sealed class ChatTicket : IDisposable
{
    private readonly ChatAdmissionQueue _owner;
    private readonly TaskCompletionSource _granted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ChatTicket(ChatAdmissionQueue owner, string userKey, long enqueuedAt)
    {
        _owner = owner;
        UserKey = userKey;
        EnqueuedAt = enqueuedAt;
    }

    internal string UserKey { get; }
    internal long EnqueuedAt { get; }
    internal LinkedListNode<ChatTicket>? Node { get; set; }
    internal TicketState State { get; set; } = TicketState.Waiting;

    /// <summary>True when the request had to wait in line at admission (so a slot was not free).</summary>
    public bool Queued { get; internal set; }

    /// <summary>Requests waiting in front of this one at the moment it was admitted (0 when it did not wait). A snapshot only.</summary>
    public int Ahead { get; internal set; }

    internal void Grant() => _granted.TrySetResult();

    /// <summary>
    /// Returns once a slot is held. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled (the request leaves the queue) and <see cref="AiServiceException"/> (503 AI_BUSY) when it waited longer
    /// than <c>MaxQueueWaitSeconds</c>. In both cases the ticket is already released.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (_granted.Task.IsCompletedSuccessfully)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var options = _owner.Options;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaxQueueWaitSeconds), _owner.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _granted.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            var callerGone = cancellationToken.IsCancellationRequested;
            _owner.Release(this, callerGone ? ReleaseReason.Cancelled : ReleaseReason.TimedOut);
            if (callerGone) throw;
            throw AiServiceException.Busy(options.RetryAfterSeconds, "queue-wait-timeout");
        }

        // Granted and cancelled at the same moment: give the slot straight back, Ollama must not be called.
        if (cancellationToken.IsCancellationRequested)
        {
            _owner.Release(this, ReleaseReason.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose() => _owner.Release(this, ReleaseReason.Finished);
}

public static class ChatAdmissionRegistration
{
    public static IServiceCollection AddChatAdmission(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChatQueueOptions>, ChatQueueOptionsValidator>());
        services.AddOptions<ChatQueueOptions>()
            .Bind(configuration.GetSection(ChatQueueOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ChatAdmissionQueue>();
        return services;
    }
}
