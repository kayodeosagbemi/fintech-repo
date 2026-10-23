using Plooto.Ap.Domain.Common;
namespace Plooto.Ap.Domain.Entities;

public enum ApprovalStatus { Pending, Approved, Rejected }
public enum ApprovalDecision { Approve, Reject }

public class ApprovalRequest
{
    private ApprovalRequest() { }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid PaymentId { get; private set; }
    public ApprovalStatus Status { get; private set; }
    public string? ApproverEmail { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static ApprovalRequest Create(Guid companyId, Guid invoiceId, Guid paymentId) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = companyId,
        InvoiceId = invoiceId,
        PaymentId = paymentId,
        Status = ApprovalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Decisions are one-shot; resolving twice throws.</summary>
    public void Resolve(ApprovalDecision decision, string? approverEmail)
    {
        if (Status != ApprovalStatus.Pending)
            throw new DomainRuleException($"Approval already resolved ({Status}).");
        Status = decision == ApprovalDecision.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        ApproverEmail = approverEmail;
        ResolvedAt = DateTimeOffset.UtcNow;
    }
}
