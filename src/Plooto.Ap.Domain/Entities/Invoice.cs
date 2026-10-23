using Plooto.Ap.Domain.Common;
namespace Plooto.Ap.Domain.Entities;

public enum InvoiceStatus { Draft, Submitted, Approved, Rejected, Paid, Void }

public class Invoice
{
    private Invoice() { }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid ContractorId { get; private set; }
    public string InvoiceNumber { get; private set; } = default!;  // unique per company
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string? Description { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Invoices captured through the API/CSV are immediately <see cref="InvoiceStatus.Submitted"/>.</summary>
    public static Invoice Create(Guid companyId, Guid contractorId, string invoiceNumber, decimal amount,
        string currency, DateOnly dueDate, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber)) throw new ArgumentException("InvoiceNumber is required.", nameof(invoiceNumber));
        var inv = new Invoice
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            ContractorId = contractorId,
            InvoiceNumber = invoiceNumber.Trim(),
            Status = InvoiceStatus.Submitted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        inv.SetDetails(amount, currency, dueDate, description);
        return inv;
    }

    /// <summary>Edits are only legal before the invoice is approved for payment.</summary>
    public void Update(decimal amount, string currency, DateOnly dueDate, string? description)
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Submitted))
            throw new DomainRuleException($"Invoice in status {Status} cannot be edited.");
        SetDetails(amount, currency, dueDate, description);
    }

    private void SetDetails(decimal amount, string currency, DateOnly dueDate, string? description)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
        DueDate = dueDate;
        Description = description;
    }

    public void Approve()
    {
        if (Status != InvoiceStatus.Submitted)
            throw new DomainRuleException($"Only a Submitted invoice can be approved (current: {Status}).");
        Status = InvoiceStatus.Approved;
        ApprovedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Submitted to Rejected.</summary>
    public void Reject()
    {
        if (Status != InvoiceStatus.Submitted)
            throw new DomainRuleException($"Only a Submitted invoice can be rejected (current: {Status}).");
        Status = InvoiceStatus.Rejected;
    }

    public void MarkPaid()
    {
        if (Status != InvoiceStatus.Approved)
            throw new DomainRuleException($"Only an Approved invoice can be marked paid (current: {Status}).");
        Status = InvoiceStatus.Paid;
    }

    /// <summary>Soft delete: financial records are never hard-deleted.</summary>
    public void Void()
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Void)
            throw new DomainRuleException($"Invoice in status {Status} cannot be voided.");
        Status = InvoiceStatus.Void;
    }
}
