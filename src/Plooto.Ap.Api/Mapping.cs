using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Api;

/// <summary>Entity -> DTO projections. EF entities never leave the API layer.</summary>
internal static class Mapping
{
    public static CompanyResponse ToResponse(this Company c, string? apiKey = null) =>
        new(c.Id, c.Name, c.Slug, c.CreatedAt, apiKey);

    public static ContractorResponse ToResponse(this Contractor c) =>
        new(c.Id, c.CompanyId, c.Name, c.Email, c.PayoutAccountRef, c.IsActive);

    public static InvoiceResponse ToResponse(this Invoice i) =>
        new(i.Id, i.CompanyId, i.ContractorId, i.InvoiceNumber, i.Amount, i.Currency, i.Description, i.Status.ToString(),
            i.DueDate, i.CreatedAt, i.ApprovedAt);

    public static PaymentResponse ToResponse(this Payment p) =>
        new(p.Id, p.CompanyId, p.InvoiceId, p.ContractorId, p.Amount, p.Currency, p.Status.ToString(), p.Method.ToString(),
            p.BatchId, p.Rail, p.CreatedAt, p.CompletedAt, p.FailureReason);

    public static ApprovalResponse ToResponse(this ApprovalRequest a) =>
        new(a.Id, a.CompanyId, a.InvoiceId, a.PaymentId, a.Status.ToString(), a.ApproverEmail, a.CreatedAt, a.ResolvedAt);
}
