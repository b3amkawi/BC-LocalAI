using System.Diagnostics.Metrics;

namespace BC_LocalAI.Services.ChatAdmission;

/// <summary>
/// BE-008: counters for the gate, published as System.Diagnostics.Metrics (meter <see cref="MeterName"/>) for OPS-002 to
/// collect, and as plain totals for tests and health output. They hold numbers only: no user id, question, answer or token.
/// </summary>
public sealed class ChatQueueMetrics
{
    public const string MeterName = "BC-LocalAI.ChatQueue";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _waitTimedOut;
    private readonly Counter<long> _admitted;
    private readonly Histogram<double> _waitSeconds;
    private long _rejectedUserQuota, _rejectedQueueFull, _cancelledTotal, _waitTimedOutTotal, _admittedTotal;

    internal ChatQueueMetrics(ChatAdmissionQueue queue)
    {
        _meter.CreateObservableGauge("bc.chat.queue.active", () => queue.Snapshot().Active, description: "Requests being served by Ollama now");
        _meter.CreateObservableGauge("bc.chat.queue.waiting", () => queue.Snapshot().Queued, description: "Requests waiting for a slot now");
        _admitted = _meter.CreateCounter<long>("bc.chat.queue.admitted", description: "Requests that obtained a slot");
        _rejected = _meter.CreateCounter<long>("bc.chat.queue.rejected", description: "Requests refused at admission (reason: user-quota or queue-full)");
        _cancelled = _meter.CreateCounter<long>("bc.chat.queue.cancelled", description: "Requests that left the queue because the caller went away");
        _waitTimedOut = _meter.CreateCounter<long>("bc.chat.queue.wait_timeout", description: "Requests that waited longer than MaxQueueWaitSeconds");
        _waitSeconds = _meter.CreateHistogram<double>("bc.chat.queue.wait", unit: "s", description: "Time between admission and obtaining a slot");
    }

    public long RejectedUserQuotaTotal => Interlocked.Read(ref _rejectedUserQuota);
    public long RejectedQueueFullTotal => Interlocked.Read(ref _rejectedQueueFull);
    public long CancelledTotal => Interlocked.Read(ref _cancelledTotal);
    public long WaitTimedOutTotal => Interlocked.Read(ref _waitTimedOutTotal);
    public long AdmittedTotal => Interlocked.Read(ref _admittedTotal);

    internal void RejectedUserQuota()
    {
        Interlocked.Increment(ref _rejectedUserQuota);
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", "user-quota"));
    }

    internal void RejectedQueueFull()
    {
        Interlocked.Increment(ref _rejectedQueueFull);
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", "queue-full"));
    }

    internal void Cancelled()
    {
        Interlocked.Increment(ref _cancelledTotal);
        _cancelled.Add(1);
    }

    internal void WaitTimedOut()
    {
        Interlocked.Increment(ref _waitTimedOutTotal);
        _waitTimedOut.Add(1);
    }

    internal void Admitted(TimeSpan waited)
    {
        Interlocked.Increment(ref _admittedTotal);
        _admitted.Add(1);
        _waitSeconds.Record(waited.TotalSeconds);
    }
}
