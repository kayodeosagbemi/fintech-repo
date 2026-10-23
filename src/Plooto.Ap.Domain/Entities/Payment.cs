namespace Plooto.Ap.Domain.Entities;

public enum PaymentStatus { Pending, Approved, Processing, Completed, Failed, Cancelled }
public enum PaymentMethod { Single, Batch }

public class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid ContractorId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public PaymentStatus Status { get; private set; }
    public PaymentMethod Method { get; private set; }
    public Guid? BatchId { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string? Rail { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>Statuses in which a payment still "owns" its invoice (blocks a second payment).</summary>
    public static readonly PaymentStatus[] ActiveStatuses =
        { PaymentStatus.Pending, PaymentStatus.Approved, PaymentStatus.Processing, PaymentStatus.Completed };

    public static Payment Create(Guid companyId, Guid invoiceId, Guid contractorId, decimal amount, string currency,
        PaymentMethod method, string idempotencyKey, Guid? batchId = null, string? rail = null)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));
        return new Payment
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            InvoiceId = invoiceId,
            ContractorId = contractorId,
            Amount = amount,
            Currency = currency,
            Method = method,
            BatchId = batchId,
            IdempotencyKey = idempotencyKey,
            Rail = rail,
            Status = PaymentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Pending to Approved (auto-approved by policy, or by a human approver).</summary>
    public void Approve()
    {
        Require(Status == PaymentStatus.Pending, "approved", PaymentStatus.Pending);
        Status = PaymentStatus.Approved;
    }

    /// <summary>Pending to Cancelled (approver rejected).</summary>
    public void MarkCancelled(string reason)
    {
        Require(Status == PaymentStatus.Pending, "cancelled", PaymentStatus.Pending);
        Status = PaymentStatus.Cancelled;
        FailureReason = reason;
    }

    public void MarkProcessing()
    {
        Require(Status == PaymentStatus.Approved, "processed", PaymentStatus.Approved);
        Status = PaymentStatus.Processing;
    }

    public void MarkCompleted()
    {
        Require(Status == PaymentStatus.Processing, "completed", PaymentStatus.Processing);
        Status = PaymentStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status is not (PaymentStatus.Approved or PaymentStatus.Processing))
            throw new InvalidOperationException($"Payment in status {Status} cannot fail.");
        Status = PaymentStatus.Failed;
        FailureReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    private void Require(bool ok, string verb, PaymentStatus expected)
    {
        if (!ok) throw new InvalidOperationException($"Payment can only be {verb} from {expected} (current: {Status}).");
    }
}
