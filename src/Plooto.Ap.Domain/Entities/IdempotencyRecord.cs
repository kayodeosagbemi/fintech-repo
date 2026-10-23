namespace Plooto.Ap.Domain.Entities;

public enum IdempotencyStatus { InProgress, Completed }

/// <summary>The "tb" ledger: remembers the outcome of a keyed request so retries replay instead of re-executing.</summary>
public class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public Guid CompanyId { get; private set; }
    public string Key { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public IdempotencyStatus Status { get; private set; }
    public int ResponseStatusCode { get; private set; }
    public string ResponseBody { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static IdempotencyRecord Begin(Guid companyId, string key, string requestHash)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(requestHash)) throw new ArgumentException("RequestHash is required.", nameof(requestHash));
        return new IdempotencyRecord
        {
            CompanyId = companyId,
            Key = key,
            RequestHash = requestHash,
            Status = IdempotencyStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Complete(int statusCode, string? body)
    {
        if (Status == IdempotencyStatus.Completed) throw new InvalidOperationException("Idempotency record already completed.");
        Status = IdempotencyStatus.Completed;
        ResponseStatusCode = statusCode;
        ResponseBody = body ?? "";
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
