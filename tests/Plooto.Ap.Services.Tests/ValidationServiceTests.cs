using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Services.Tests.TestSupport;

namespace Plooto.Ap.Services.Tests;

public class ValidationServiceTests
{
    [Fact]
    public async Task ValidInvoice_ReturnsOkWithContext()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);

        var r = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);

        Assert.Equal(ServiceOutcome.Ok, r.Outcome);
        Assert.Equal(inv.Id, r.Value!.Invoice.Id);
        Assert.Equal("ACH", r.Value.Preferences.PreferredPaymentRail);
    }

    [Fact]
    public async Task UnknownInvoice_ReturnsNotFound()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, _) = await ServiceTestHarness.SeedAsync(s.Db);
        var r = await s.Validation.ValidatePaymentAsync(co.Id, Guid.NewGuid(), default);
        Assert.Equal(ServiceOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task InvoiceOfAnotherCompany_ReturnsNotFound_NotLeakingExistence()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (_, _, otherInvoice) = await ServiceTestHarness.SeedAsync(s.Db, "other");
        var (co, _, _) = await ServiceTestHarness.SeedAsync(s.Db, "mine");
        var r = await s.Validation.ValidatePaymentAsync(co.Id, otherInvoice.Id, default);
        Assert.Equal(ServiceOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task PaidInvoice_ReturnsConflict()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        inv.Approve();
        inv.MarkPaid();
        await s.Db.SaveChangesAsync();
        var r = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);
        Assert.Equal(ServiceOutcome.Conflict, r.Outcome);
        Assert.Contains("already been paid", r.Error);
    }

    [Fact]
    public async Task VoidInvoice_ReturnsConflict()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        inv.Void();
        await s.Db.SaveChangesAsync();
        Assert.Equal(ServiceOutcome.Conflict, (await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default)).Outcome);
    }

    [Fact]
    public async Task InactiveContractor_ReturnsConflict()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, contractor, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        contractor.Deactivate();
        await s.Db.SaveChangesAsync();
        Assert.Equal(ServiceOutcome.Conflict, (await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default)).Outcome);
    }

    [Fact]
    public async Task InvoiceWithActivePayment_ReturnsConflict()
    {
        using var h = new ServiceTestHarness();
        await using var s = h.NewScope();
        var (co, _, inv) = await ServiceTestHarness.SeedAsync(s.Db);
        var validated = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);
        await s.Payments.CreatePaymentAsync(validated.Value!, "k1", PaymentMethod.Single, null, default);

        var again = await s.Validation.ValidatePaymentAsync(co.Id, inv.Id, default);
        Assert.Equal(ServiceOutcome.Conflict, again.Outcome);
    }
}
