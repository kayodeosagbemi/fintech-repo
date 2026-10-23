namespace Plooto.Ap.Domain.Entities;

public enum OutboxStatus { Pending, Dispatched, Processed, DeadLettered }

/// <summary>
/// The "pymtevt" outbox row. Written in the same transaction as the domain change it describes; the
/// OutboxDispatcher relays it onto a named in-process queue. The row (not the channel message) is the source of
/// truth for whether work happened. See PLAN_1.md section 5.4.
/// </summary>
public class PaymentEvent
{
    public const int MaxAttempts = 5;

    private PaymentEvent() { }

    public long Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid CompanyId { get; private set; }
    public string EventType { get; private set; } = default!;
    public string QueueName { get; private set; } = default!;
    public string PayloadJson { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }   // set when the consumer finishes (Processed)
    public string? LastError { get; private set; }

    public static PaymentEvent Create(Guid paymentId, Guid companyId, string eventType, string queueName, string payloadJson)
    {
        var now = DateTimeOffset.UtcNow;
        return new PaymentEvent
        {
            PaymentId = paymentId,
            CompanyId = companyId,
            EventType = eventType,
            QueueName = queueName,
            PayloadJson = payloadJson,
            OccurredAt = now,
            NextAttemptAt = now,
            Status = OutboxStatus.Pending,
        };
    }

    public void MarkDispatched(DateTimeOffset now)
    {
        Status = OutboxStatus.Dispatched;
        DispatchedAt = now;
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = OutboxStatus.Processed;
        PublishedAt = now;
        LastError = null;
    }

    /// <summary>Records a failed publish/consume attempt: exponential backoff, then dead-letter after <see cref="MaxAttempts"/>.</summary>
    public void RecordFailure(string error, DateTimeOffset now)
    {
        AttemptCount++;
        LastError = error.Length > 1000 ? error[..1000] : error;
        if (AttemptCount >= MaxAttempts)
        {
            Status = OutboxStatus.DeadLettered;
            return;
        }
        Status = OutboxStatus.Pending;
        NextAttemptAt = now.AddSeconds(Math.Min(Math.Pow(2, AttemptCount), 60));
    }

    /// <summary>Re-queues a row stuck in Dispatched (consumer crashed before finishing).</summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = OutboxStatus.Pending;
        NextAttemptAt = now;
    }
}
