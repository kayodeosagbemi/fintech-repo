using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging;

namespace Plooto.Ap.Services.Payment;

using PaymentEntity = Domain.Entities.Payment;

public interface IPaymentService
{
    /// <summary>
    /// Persists a validated payment (plus approval request and outbox event) in ONE transaction. Safe to call twice with
    /// the same key: the original payment is returned and no second row is written.
    /// </summary>
    Task<ServiceResult<PaymentEntity>> CreatePaymentAsync(ValidatedPaymentRequest request, string idempotencyKey,
        PaymentMethod method, Guid? batchId, CancellationToken ct);

    Task<PaymentEntity?> FindByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken ct);

    /// <summary>Approver decision. Approve releases the payment to processing; Reject cancels it. One-shot.</summary>
    Task<ServiceResult<PaymentEntity>> ResolveApprovalAsync(Guid companyId, Guid approvalId, ApprovalDecision decision,
        string? approverEmail, CancellationToken ct);

    /// <summary>Approved -> Processing. Returns null if the payment is not in a state that can be processed (already handled).</summary>
    Task<PaymentEntity?> BeginProcessingAsync(Guid paymentId, CancellationToken ct);
    Task<PaymentEntity?> CompletePaymentAsync(Guid paymentId, CancellationToken ct);
    Task<PaymentEntity?> FailPaymentAsync(Guid paymentId, string reason, CancellationToken ct);
}

public sealed class PaymentService(ApDbContext db, IEventPublisher events) : IPaymentService
{
    public async Task<PaymentEntity?> FindByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken ct) =>
        await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task<ServiceResult<PaymentEntity>> CreatePaymentAsync(ValidatedPaymentRequest request, string idempotencyKey,
        PaymentMethod method, Guid? batchId, CancellationToken ct)
    {
        var (invoice, contractor, prefs) = request;

        var existing = await FindByIdempotencyKeyAsync(invoice.CompanyId, idempotencyKey, ct);
        if (existing is not null)
            return ServiceResult<PaymentEntity>.Replayed(existing);

        if (invoice.CompanyId != contractor.CompanyId)
            return ServiceResult<PaymentEntity>.Invalid("Invoice and contractor belong to different companies.");

        var payment = PaymentEntity.Create(invoice.CompanyId, invoice.Id, contractor.Id, invoice.Amount, invoice.Currency,
            method, idempotencyKey, batchId, prefs.PreferredPaymentRail);
        db.Payments.Add(payment);

        if (prefs.RequiresApproval(payment.Amount))
        {
            // Stays Pending until a human decides; nothing goes onto validated-payments yet.
            var approval = ApprovalRequest.Create(invoice.CompanyId, invoice.Id, payment.Id);
            db.ApprovalRequests.Add(approval);
            await events.PublishAsync(new ApprovalRequested(payment.Id, payment.CompanyId, approval.Id, invoice.Id,
                payment.Amount, payment.Currency), QueueNames.Notifications, ct);
        }
        else
        {
            payment.Approve();   // auto-approved by company policy
            if (invoice.Status == InvoiceStatus.Submitted) invoice.Approve();
            await events.PublishAsync(new PaymentValidated(payment.Id, payment.CompanyId), QueueNames.ValidatedPayments, ct);
        }

        try
        {
            // Payment + approval + outbox rows commit atomically in this single SaveChanges.
            await db.SaveChangesAsync(ct);
            return ServiceResult<PaymentEntity>.Ok(payment);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Lost a race: the DB unique indexes are the real duplicate-payment guard.
            db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKeyAsync(invoice.CompanyId, idempotencyKey, ct);
            return winner is not null
                ? ServiceResult<PaymentEntity>.Replayed(winner)
                : ServiceResult<PaymentEntity>.Conflict($"Invoice {invoice.InvoiceNumber} already has an active payment.");
        }
    }

    public async Task<ServiceResult<PaymentEntity>> ResolveApprovalAsync(Guid companyId, Guid approvalId,
        ApprovalDecision decision, string? approverEmail, CancellationToken ct)
    {
        var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == approvalId, ct);
        if (approval is null)
            return ServiceResult<PaymentEntity>.NotFound($"Approval {approvalId} was not found.");
        if (approval.Status != ApprovalStatus.Pending)
            return ServiceResult<PaymentEntity>.Conflict($"Approval was already resolved ({approval.Status}).");

        var payment = await db.Payments.FirstAsync(p => p.CompanyId == companyId && p.Id == approval.PaymentId, ct);
        approval.Resolve(decision, approverEmail);

        if (decision == ApprovalDecision.Approve)
        {
            var invoice = await db.Invoices.FirstAsync(i => i.CompanyId == companyId && i.Id == payment.InvoiceId, ct);
            payment.Approve();
            if (invoice.Status == InvoiceStatus.Submitted) invoice.Approve();
            await events.PublishAsync(new PaymentValidated(payment.Id, companyId), QueueNames.ValidatedPayments, ct);
        }
        else
        {
            // Invoice stays Submitted so it can be resubmitted with a new payment and idempotency key.
            payment.MarkCancelled("Rejected by approver");
            await events.PublishAsync(new PaymentNotification(payment.Id, companyId, payment.Status.ToString(),
                "Payment rejected by approver"), QueueNames.Notifications, ct);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<PaymentEntity>.Ok(payment);
    }

    public async Task<PaymentEntity?> BeginProcessingAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.Approved) return null;
        payment.MarkProcessing();
        await db.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<PaymentEntity?> CompletePaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.Processing) return null;
        var invoice = await db.Invoices.FirstAsync(i => i.Id == payment.InvoiceId, ct);
        payment.MarkCompleted();
        invoice.MarkPaid();
        await events.PublishAsync(new PaymentProcessed(payment.Id, payment.CompanyId), QueueNames.PaymentEventsCdc, ct);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<PaymentEntity?> FailPaymentAsync(Guid paymentId, string reason, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status is not (PaymentStatus.Approved or PaymentStatus.Processing)) return null;
        payment.MarkFailed(reason);
        await events.PublishAsync(new PaymentFailed(payment.Id, payment.CompanyId, reason), QueueNames.PaymentEventsCdc, ct);
        await db.SaveChangesAsync(ct);
        return payment;
    }
}
