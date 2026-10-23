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
    public void MarkFailed_WhenAlreadyCompleted_Throws()
    {
        var p = NewPayment();
        p.Approve();
        p.MarkProcessing();
        p.MarkCompleted();
        Assert.Throws<InvalidOperationException>(() => p.MarkFailed("late"));
    }
}
