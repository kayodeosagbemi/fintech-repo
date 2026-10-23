using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Tests.Domain;

public class InvoiceTests
{
    private static Invoice NewInvoice(decimal amount = 100m) =>
        Invoice.Create(Guid.NewGuid(), Guid.NewGuid(), "INV-1", amount, "usd", new DateOnly(2026, 12, 1));

    [Fact]
    public void Create_ValidInput_IsSubmittedWithNormalizedCurrency()
    {
        var inv = NewInvoice();
        Assert.Equal(InvoiceStatus.Submitted, inv.Status);
        Assert.Equal("USD", inv.Currency);
    }

    [Fact]
    public void Create_WithZeroAmount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewInvoice(0m));

    [Fact]
    public void Create_WithNegativeAmount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewInvoice(-5m));

    [Fact]
    public void Create_WithBadCurrency_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Invoice.Create(Guid.NewGuid(), Guid.NewGuid(), "INV-1", 10m, "DOLLARS", new DateOnly(2026, 1, 1)));

    [Fact]
    public void Approve_WhenSubmitted_TransitionsToApproved()
    {
        var inv = NewInvoice();
        inv.Approve();
        Assert.Equal(InvoiceStatus.Approved, inv.Status);
        Assert.NotNull(inv.ApprovedAt);
    }

    [Fact]
    public void Approve_WhenAlreadyApproved_Throws()
    {
        var inv = NewInvoice();
        inv.Approve();
        Assert.Throws<InvalidOperationException>(inv.Approve);
    }

    [Fact]
    public void MarkPaid_WhenNotApproved_Throws() =>
        Assert.Throws<InvalidOperationException>(NewInvoice().MarkPaid);

    [Fact]
    public void MarkPaid_WhenApproved_TransitionsToPaid()
    {
        var inv = NewInvoice();
        inv.Approve();
        inv.MarkPaid();
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
    }

    [Fact]
    public void Update_WhenPaid_Throws()
    {
        var inv = NewInvoice();
        inv.Approve();
        inv.MarkPaid();
        Assert.Throws<InvalidOperationException>(() => inv.Update(5m, "USD", new DateOnly(2026, 1, 1), null));
    }

    [Fact]
    public void Void_WhenPaid_Throws()
    {
        var inv = NewInvoice();
        inv.Approve();
        inv.MarkPaid();
        Assert.Throws<InvalidOperationException>(inv.Void);
    }

    [Fact]
    public void Void_WhenSubmitted_SetsVoid()
    {
        var inv = NewInvoice();
        inv.Void();
        Assert.Equal(InvoiceStatus.Void, inv.Status);
    }
}
