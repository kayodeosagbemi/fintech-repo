using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;

namespace Plooto.Ap.Services.Validation;

public interface IValidationService
{
    /// <summary>Synchronous business-rule checks run before any payment row exists or any message is published.</summary>
    Task<ServiceResult<ValidatedPaymentRequest>> ValidatePaymentAsync(Guid companyId, Guid invoiceId, CancellationToken ct);
}

/// <summary>The "validation srv" of the design diagram.</summary>
public sealed class ValidationService(
    IInvoiceRepository invoices,
    IContractorRepository contractors,
    IPaymentRepository payments,
    ICompanyRepository companies) : IValidationService
{
    public async Task<ServiceResult<ValidatedPaymentRequest>> ValidatePaymentAsync(Guid companyId, Guid invoiceId, CancellationToken ct)
    {
        // Company-scoped lookup: an invoice id belonging to another tenant is indistinguishable from a missing one.
        var invoice = await invoices.GetAsync(companyId, invoiceId, ct);
        if (invoice is null)
            return ServiceResult<ValidatedPaymentRequest>.NotFound($"Invoice {invoiceId} was not found.");

        if (invoice.Status == InvoiceStatus.Paid)
            return ServiceResult<ValidatedPaymentRequest>.Conflict($"Invoice {invoice.InvoiceNumber} has already been paid.");
        if (invoice.Status is not (InvoiceStatus.Submitted or InvoiceStatus.Approved))
            return ServiceResult<ValidatedPaymentRequest>.Conflict($"Invoice {invoice.InvoiceNumber} is {invoice.Status} and cannot be paid.");

        var contractor = await contractors.GetAsync(companyId, invoice.ContractorId, ct);
        if (contractor is null || !contractor.IsActive)
            return ServiceResult<ValidatedPaymentRequest>.Conflict("The invoice's contractor is missing or inactive.");

        var existing = await payments.GetActiveForInvoiceAsync(companyId, invoiceId, ct);
        if (existing is not null)
            return ServiceResult<ValidatedPaymentRequest>.Conflict(
                $"Invoice {invoice.InvoiceNumber} already has a {existing.Status} payment ({existing.Id}).");

        var prefs = await companies.GetPreferencesAsync(companyId, ct) ?? CompanyPreferences.CreateDefault(companyId);
        return ServiceResult<ValidatedPaymentRequest>.Ok(new ValidatedPaymentRequest(invoice, contractor, prefs));
    }
}
