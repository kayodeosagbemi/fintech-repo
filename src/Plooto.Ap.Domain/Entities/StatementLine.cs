namespace Plooto.Ap.Domain.Entities;

/// <summary>Read model maintained by the StatementProjector from payment lifecycle events.</summary>
public class StatementLine
{
    private StatementLine() { }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid PaymentId { get; private set; }
    public string Type { get; private set; } = default!;        // "Payment" | "Failure"
    public string CounterParty { get; private set; } = default!;
    public decimal Amount { get; private set; }                  // positive number; Payment lines debit the balance
    public string Currency { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }

    public static StatementLine Create(Guid companyId, Guid paymentId, string type, string counterParty,
        decimal amount, string currency, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = companyId,
        PaymentId = paymentId,
        Type = type,
        CounterParty = counterParty,
        Amount = amount,
        Currency = currency,
        OccurredAt = occurredAt,
    };
}
