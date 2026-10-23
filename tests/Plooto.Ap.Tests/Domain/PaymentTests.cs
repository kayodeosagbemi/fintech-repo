using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Tests.Domain;

public class PaymentTests
{
    private static Payment NewPayment(decimal amount = 50m) =>
        Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, "USD", PaymentMethod.Single, "key-1");

    [Fact]
    public void Create_StartsPending() => Assert.Equal(PaymentStatus.Pending, NewPayment().Status);

    [Fact]
    public void Create_WithZeroAmount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPayment(0m));

    [Fact]
    public void Create_WithoutIdempotencyKey_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5m, "USD", PaymentMethod.Single, " "));

    [Fact]
    public void Approve_FromPending_SetsApproved()
    {
        var p = NewPayment();
        p.Approve();
        Assert.Equal(PaymentStatus.Approved, p.Status);
    }

    [Fact]
    public void Approve_Twice_Throws()
    {
        var p = NewPayment();
        p.Approve();
        Assert.Throws<InvalidOperationException>(p.Approve);
    }

    [Fact]
    public void MarkCancelled_FromPending_SetsCancelledAndReason()
    {
        var p = NewPayment();
        p.MarkCancelled("Rejected by approver");
        Assert.Equal(PaymentStatus.Cancelled, p.Status);
        Assert.Equal("Rejected by approver", p.FailureReason);
    }

    [Fact]
    public void MarkCancelled_AfterApproval_Throws()
    {
        var p = NewPayment();
        p.Approve();
        Assert.Throws<InvalidOperationException>(() => p.MarkCancelled("x"));
    }

    [Fact]
    public void MarkProcessing_FromPending_Throws() =>
        Assert.Throws<InvalidOperationException>(NewPayment().MarkProcessing);

    [Fact]
    public void MarkCompleted_FromPending_Throws() =>
        Assert.Throws<InvalidOperationException>(NewPayment().MarkCompleted);

    [Fact]
    public void MarkCompleted_FromProcessing_SetsCompletedAtAndStatus()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        p.MarkCompleted();
        Assert.Equal(PaymentStatus.Completed, p.Status);
        Assert.NotNull(p.CompletedAt);
    }

    [Fact]
    public void MarkFailed_FromProcessing_SetsFailureReasonAndStatus()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        p.MarkFailed("rail declined");
        Assert.Equal(PaymentStatus.Failed, p.Status);
        Assert.Equal("rail declined", p.FailureReason);
    }

    [Fact]
    public void Create_WithBadCurrency_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5m, "DOLLARS", PaymentMethod.Single, "k"));

    [Fact]
    public void Create_NormalizesCurrency() =>
        Assert.Equal("USD", Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5m, "usd", PaymentMethod.Single, "k").Currency);

    [Fact]
    public void Create_WithEmptyGuids_Throws()
    {
        var g = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => Payment.Create(Guid.Empty, g, g, 5m, "USD", PaymentMethod.Single, "k"));
        Assert.Throws<ArgumentException>(() => Payment.Create(g, Guid.Empty, g, 5m, "USD", PaymentMethod.Single, "k"));
        Assert.Throws<ArgumentException>(() => Payment.Create(g, g, Guid.Empty, 5m, "USD", PaymentMethod.Single, "k"));
    }

    [Fact]
    public void MarkProcessing_FromApproved_SetsProcessing()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        Assert.Equal(PaymentStatus.Processing, p.Status);
    }

    [Fact]
    public void MarkProcessing_Twice_Throws()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        Assert.Throws<InvalidOperationException>(p.MarkProcessing);
    }

    [Fact]
    public void MarkCompleted_FromApproved_Throws()
    {
        var p = NewPayment();
        p.Approve();
        Assert.Throws<InvalidOperationException>(p.MarkCompleted);
    }

    [Fact]
    public void MarkFailed_FromPending_Throws() =>
        Assert.Throws<InvalidOperationException>(() => NewPayment().MarkFailed("x"));

    [Fact]
    public void MarkFailed_FromApproved_SetsFailed()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkFailed("x");
        Assert.Equal(PaymentStatus.Failed, p.Status);
        Assert.NotNull(p.CompletedAt);
    }

    [Fact]
    public void MarkFailed_And_MarkCancelled_RequireReason()
    {
        var p = NewPayment();
        Assert.Throws<ArgumentException>(() => p.MarkCancelled(" "));
        p.Approve();
        Assert.Throws<ArgumentException>(() => p.MarkFailed(""));
    }

    [Fact]
    public void MarkCancelled_SetsCompletedAt()
    {
        var p = NewPayment();
        p.MarkCancelled("no");
        Assert.NotNull(p.CompletedAt);
    }

    [Fact]
    public void MarkFailed_WhenAlreadyCompleted_Throws()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        p.MarkCompleted();
        Assert.Throws<InvalidOperationException>(() => p.MarkFailed("late"));
    }
}
