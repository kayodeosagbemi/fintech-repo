using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Messaging;
using Plooto.Ap.Services.Tests.TestSupport;

namespace Plooto.Ap.Services.Tests;

public class PaymentServiceTests
{
    private static async Task<ValidatedPaymentRequest> ValidateAsync(ServiceTestHarness.Scope s, Company co, Invoice inv) =>
        (await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default)).Value!;

    [Fact]
    public async Task CreatePayment_HappyPath_WritesPaymentInvoiceAndOutboxEventTogether()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);

        var result = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        await using var read = h.NewContext();
        var payment = await read.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Approved, payment.Status);
        Assert.Equal("ACH", payment.Rail);
        Assert.Equal(InvoiceStatus.Approved, (await read.Invoices.SingleAsync()).Status);
        var evt = await read.PaymentEvents.SingleAsync();
        Assert.Equal(QueueNames.ValidatedPayments, evt.QueueName);
        Assert.Equal(nameof(PaymentValidated), evt.EventType);
        Assert.Equal(payment.Id, evt.PaymentId);
        Assert.Equal(OutboxStatus.Pending, evt.Status);
    }

    [Fact]
    public async Task CreatePayment_CalledTwiceWithSameIdempotencyKey_ReturnsOriginalPaymentNoDuplicateRow()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        var validated = await ValidateAsync(s, co, inv);

        var first = await s.Payments.CreatePaymentAsync(validated, "same-key", PaymentMethod.Single, null, default);
        var second = await s.Payments.CreatePaymentAsync(validated, "same-key", PaymentMethod.Single, null, default);

        Assert.Equal(ServiceOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        await using var read = h.NewContext();
        Assert.Equal(1, await read.Payments.CountAsync());
        Assert.Equal(1, await read.PaymentEvents.CountAsync());
    }

    [Fact]
    public async Task CreatePayment_AboveApprovalThreshold_StaysPendingAndRequestsApproval()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, amount: 9000m, requireApproval: true, threshold: 5000m);

        var result = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);

        Assert.Equal(PaymentStatus.Pending, result.Value!.Status);
        await using var read = h.NewContext();
        Assert.Equal(ApprovalStatus.Pending, (await read.ApprovalRequests.SingleAsync()).Status);
        Assert.Equal(InvoiceStatus.Submitted, (await read.Invoices.SingleAsync()).Status);
        var evt = await read.PaymentEvents.SingleAsync();
        Assert.Equal(QueueNames.Notifications, evt.QueueName);
        Assert.Equal(nameof(ApprovalRequested), evt.EventType);
    }

    [Fact]
    public async Task CreatePayment_AtThreshold_IsNotGatedByApproval()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, amount: 5000m, requireApproval: true, threshold: 5000m);
        var result = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);
        Assert.Equal(PaymentStatus.Approved, result.Value!.Status);
    }

    [Fact]
    public async Task CreatePayment_ContractorFromAnotherCompany_IsInvalid()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, "a");
        var (_, foreignContractor, _) = await ServiceTestHarness.SeedAsync(s.Db, "b");
        var prefs = CompanyPreferences.CreateDefault(co.Id);

        var result = await s.Payments.CreatePaymentAsync(new ValidatedPaymentRequest(inv, foreignContractor, prefs),
            "k1", PaymentMethod.Single, null, default);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        await using var read = h.NewContext();
        Assert.Equal(0, await read.Payments.CountAsync());
    }

    [Fact]
    public async Task CreatePayment_ConcurrentSecondPaymentForSameInvoice_LosesToDbConstraintAsConflict()
    {
        using var h = new ServiceTestHarness();
        await using var s1 = h.NewScope();
        await using var s2 = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s1.Db);
        // Both requests validate before either commits - the race the unique index exists for.
        var v1 = await ValidateAsync(s1, co, inv);
        var v2 = await ValidateAsync(s2, co, inv);

        var r1 = await s1.Payments.CreatePaymentAsync(v1, "key-a", PaymentMethod.Single, null, default);
        var r2 = await s2.Payments.CreatePaymentAsync(v2, "key-b", PaymentMethod.Single, null, default);

        Assert.Equal(ServiceOutcome.Ok, r1.Outcome);
        Assert.Equal(ServiceOutcome.Conflict, r2.Outcome);
        await using var read = h.NewContext();
        Assert.Equal(1, await read.Payments.CountAsync());
    }

    [Fact]
    public async Task CreatePayment_ConcurrentSameKey_LoserGetsWinnersPayment()
    {
        using var h = new ServiceTestHarness();
        await using var s1 = h.NewScope();
        await using var s2 = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s1.Db);
        var v1 = await ValidateAsync(s1, co, inv);
        var v2 = await ValidateAsync(s2, co, inv);

        var r1 = await s1.Payments.CreatePaymentAsync(v1, "same", PaymentMethod.Single, null, default);
        var r2 = await s2.Payments.CreatePaymentAsync(v2, "same", PaymentMethod.Single, null, default);

        Assert.Equal(ServiceOutcome.Ok, r1.Outcome);
        Assert.Equal(ServiceOutcome.Replayed, r2.Outcome);
        Assert.Equal(r1.Value!.Id, r2.Value!.Id);
    }

    [Fact]
    public async Task ProcessingLifecycle_CompleteMarksPaymentAndInvoicePaid_AndEmitsEvent()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        var created = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);

        Assert.NotNull(await s.Payments.BeginProcessingAsync(created.Value!.Id, default));
        Assert.NotNull(await s.Payments.CompletePaymentAsync(created.Value.Id, default));

        await using var read = h.NewContext();
        Assert.Equal(PaymentStatus.Completed, (await read.Payments.SingleAsync()).Status);
        Assert.Equal(InvoiceStatus.Paid, (await read.Invoices.SingleAsync()).Status);
        Assert.Contains(await read.PaymentEvents.ToListAsync(),
            e => e.EventType == nameof(PaymentProcessed) && e.QueueName == QueueNames.PaymentEventsCdc);
    }

    [Fact]
    public async Task CompletePayment_InvalidTransition_ReturnsNullAndChangesNothing()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        var created = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);

        // Approved (never began processing) cannot be completed.
        Assert.Null(await s.Payments.CompletePaymentAsync(created.Value!.Id, default));
        Assert.Null(await s.Payments.CompletePaymentAsync(Guid.NewGuid(), default));
        await using var read = h.NewContext();
        Assert.Equal(PaymentStatus.Approved, (await read.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task FailPayment_MarksFailed_InvoiceStaysUnpaid_AndInvoiceCanBeRetriedWithNewKey()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        var created = await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);
        await s.Payments.BeginProcessingAsync(created.Value!.Id, default);

        var failed = await s.Payments.FailPaymentAsync(created.Value.Id, "rail declined", default);

        Assert.Equal(PaymentStatus.Failed, failed!.Status);
        var retry = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);
        Assert.Equal(ServiceOutcome.Ok, retry.Outcome);
        var second = await s.Payments.CreatePaymentAsync(retry.Value!, "k2", PaymentMethod.Single, null, default);
        Assert.Equal(ServiceOutcome.Ok, second.Outcome);
        // Retrying with the SAME key replays the original (failed) payment by design.
        var replay = await s.Payments.CreatePaymentAsync(retry.Value!, "k1", PaymentMethod.Single, null, default);
        Assert.Equal(ServiceOutcome.Replayed, replay.Outcome);
        Assert.Equal(PaymentStatus.Failed, replay.Value!.Status);
    }

    [Fact]
    public async Task ResolveApproval_Approve_ReleasesPaymentToProcessing()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, amount: 9000m, requireApproval: true, threshold: 100m);
        await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);
        var approvalId = (await s.Db.ApprovalRequests.SingleAsync()).Id;

        var result = await s.Payments.ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Approve, "boss@example.com", default);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.Equal(PaymentStatus.Approved, result.Value!.Status);
        await using var read = h.NewContext();
        Assert.Equal(InvoiceStatus.Approved, (await read.Invoices.SingleAsync()).Status);
        Assert.Contains(await read.PaymentEvents.ToListAsync(), e => e.EventType == nameof(PaymentValidated));
    }

    [Fact]
    public async Task ResolveApproval_Reject_CancelsPayment_KeepsInvoiceSubmitted_AndNotifies()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, amount: 9000m, requireApproval: true, threshold: 100m);
        await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);
        var approvalId = (await s.Db.ApprovalRequests.SingleAsync()).Id;

        var result = await s.Payments.ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Reject, "boss@example.com", default);

        Assert.Equal(PaymentStatus.Cancelled, result.Value!.Status);
        await using var read = h.NewContext();
        Assert.Equal(InvoiceStatus.Submitted, (await read.Invoices.SingleAsync()).Status);
        Assert.Equal(ApprovalStatus.Rejected, (await read.ApprovalRequests.SingleAsync()).Status);
        var events = await read.PaymentEvents.ToListAsync();
        Assert.DoesNotContain(events, e => e.QueueName == QueueNames.ValidatedPayments);
        Assert.Contains(events, e => e.EventType == nameof(PaymentNotification));

        // Invoice is resubmittable with a new idempotency key after rejection.
        var retry = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);
        Assert.Equal(ServiceOutcome.Ok, retry.Outcome);
    }

    [Fact]
    public async Task ResolveApproval_SecondDecision_ReturnsConflict_AndUnknownOrForeignIsNotFound()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db, amount: 9000m, requireApproval: true, threshold: 100m);
        var (other, _, _) = await ServiceTestHarness.SeedAsync(s.Db, "other");
        await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), "k1", PaymentMethod.Single, null, default);
        var approvalId = (await s.Db.ApprovalRequests.SingleAsync()).Id;

        Assert.Equal(ServiceOutcome.NotFound, (await s.Payments.ResolveApprovalAsync(other.Id, approvalId, ApprovalDecision.Approve, null, default)).Outcome);
        Assert.Equal(ServiceOutcome.NotFound, (await s.Payments.ResolveApprovalAsync(co.Id, Guid.NewGuid(), ApprovalDecision.Approve, null, default)).Outcome);
        Assert.Equal(ServiceOutcome.Ok, (await s.Payments.ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Approve, null, default)).Outcome);
        Assert.Equal(ServiceOutcome.Conflict, (await s.Payments.ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Reject, null, default)).Outcome);
    }

    [Fact]
    public async Task CreateBatchPayments_ShareBatchId()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, contractor, inv1) = await ServiceTestHarness.SeedAsync(s.Db);
        var inv2 = await ServiceTestHarness.AddInvoiceAsync(s.Db, co, contractor, "INV-2");
        var batchId = Guid.NewGuid();

        foreach (var inv in new[] { inv1, inv2 })
            await s.Payments.CreatePaymentAsync(await ValidateAsync(s, co, inv), $"batch-1:{inv.Id}", PaymentMethod.Batch, batchId, default);

        await using var read = h.NewContext();
        var payments = await read.Payments.ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.All(payments, p => Assert.Equal(batchId, p.BatchId));
        Assert.All(payments, p => Assert.Equal(PaymentMethod.Batch, p.Method));
    }
}
