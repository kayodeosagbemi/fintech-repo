using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Services.Payment;
using Plooto.Ap.Services.Validation;

namespace Plooto.Ap.Api;

/// <summary>
/// The "tb" router of the design diagram: the one place that sequences Validation Service then Payment Service. The two
/// services never call each other (PLAN_1 section 4), so this thin coordinator lives in the host.
/// </summary>
public sealed class PaymentOrchestrator(IValidationService validation, IPaymentService payments)
{
    public const int MaxBatchSize = 100;

    public async Task<ServiceResult<Domain.Entities.Payment>> CreateAsync(Guid companyId, Guid invoiceId, string idempotencyKey,
        PaymentMethod method, Guid? batchId, CancellationToken ct)
    {
        // Replay check comes first: validation would (correctly) reject a retry because the first attempt already
        // holds the invoice, but a retried key must return the original payment instead.
        var existing = await payments.FindByIdempotencyKeyAsync(companyId, idempotencyKey, ct);
        if (existing is not null)
        {
            return existing.InvoiceId == invoiceId
                ? ServiceResult<Domain.Entities.Payment>.Replayed(existing)
                : ServiceResult<Domain.Entities.Payment>.Conflict("Idempotency key was already used for a different invoice.");
        }

        var validated = await validation.ValidatePaymentAsync(companyId, invoiceId, ct);
        if (!validated.IsSuccess)
            return new ServiceResult<Domain.Entities.Payment>(validated.Outcome, null, validated.Error);

        return await payments.CreatePaymentAsync(validated.Value!, idempotencyKey, method, batchId, ct);
    }

    /// <summary>
    /// Items are independent (partial success is representable, no all-or-nothing transaction). Each gets its own derived
    /// idempotency key, and the DB's one-live-payment-per-invoice index stops the same invoice being paid twice even
    /// across different batch calls.
    /// </summary>
    public async Task<BatchPaymentResponse> CreateBatchAsync(Guid companyId, IReadOnlyList<Guid> invoiceIds, string batchKey, CancellationToken ct)
    {
        var batchId = Guid.NewGuid();
        var results = new List<BatchPaymentResult>(invoiceIds.Count);
        var seen = new HashSet<Guid>();

        foreach (var invoiceId in invoiceIds)
        {
            if (!seen.Add(invoiceId))
            {
                results.Add(new BatchPaymentResult(invoiceId, StatusCodes.Status409Conflict, "Rejected", null,
                    "Invoice appears more than once in this batch."));
                continue;
            }

            var r = await CreateAsync(companyId, invoiceId, $"{batchKey}:{invoiceId}", PaymentMethod.Batch, batchId, ct);
            results.Add(r.IsSuccess
                ? new BatchPaymentResult(invoiceId, Http.StatusFor(r.Outcome), r.Outcome == ServiceOutcome.Ok ? "Created" : "Replayed", r.Value!.Id, null)
                : new BatchPaymentResult(invoiceId, Http.StatusFor(r.Outcome), "Rejected", null, r.Error));
        }
        return new BatchPaymentResponse(batchId, results);
    }
}
