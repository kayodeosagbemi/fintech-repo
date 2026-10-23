using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Tests.Domain;

public class OtherEntityTests
{
    [Fact]
    public void ApprovalRequest_Resolve_IsOneShot()
    {
        var a = ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        a.Resolve(ApprovalDecision.Approve, "boss@example.com");
        Assert.Equal(ApprovalStatus.Approved, a.Status);
        Assert.Throws<InvalidOperationException>(() => a.Resolve(ApprovalDecision.Reject, null));
    }

    [Fact]
    public void ApprovalRequest_Reject_SetsRejected()
    {
        var a = ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        a.Resolve(ApprovalDecision.Reject, "boss@example.com");
        Assert.Equal(ApprovalStatus.Rejected, a.Status);
    }

    [Fact]
    public void CompanyPreferences_RequiresApproval_OnlyAboveThreshold()
    {
        var prefs = CompanyPreferences.Create(Guid.NewGuid(), "usd", true, 1000m, "ACH");
        Assert.False(prefs.RequiresApproval(1000m));
        Assert.True(prefs.RequiresApproval(1000.01m));
        Assert.False(CompanyPreferences.CreateDefault(Guid.NewGuid()).RequiresApproval(1_000_000m));
    }

    [Fact]
    public void Contractor_Create_NormalizesEmail_AndDeactivateKeepsRecord()
    {
        var c = Contractor.Create(Guid.NewGuid(), "Jane", " Jane@Example.COM ", "acct_1");
        Assert.Equal("jane@example.com", c.Email);
        c.Deactivate();
        Assert.False(c.IsActive);
    }

    [Fact]
    public void Contractor_Create_InvalidEmail_Throws() =>
        Assert.Throws<ArgumentException>(() => Contractor.Create(Guid.NewGuid(), "Jane", "nope", "acct_1"));

    [Fact]
    public void IdempotencyRecord_CompleteTwice_Throws()
    {
        var r = IdempotencyRecord.Begin(Guid.NewGuid(), "k", "hash");
        r.Complete(201, "{}");
        Assert.Equal(IdempotencyStatus.Completed, r.Status);
        Assert.Throws<InvalidOperationException>(() => r.Complete(200, "{}"));
    }

    [Fact]
    public void PaymentEvent_RecordFailure_BacksOffThenDeadLetters()
    {
        var e = PaymentEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "T", "q", "{}");
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i < PaymentEvent.MaxAttempts; i++)
        {
            e.RecordFailure("boom", now);
            Assert.Equal(OutboxStatus.Pending, e.Status);
            Assert.True(e.NextAttemptAt > now);
        }
        e.RecordFailure("boom", now);
        Assert.Equal(OutboxStatus.DeadLettered, e.Status);
        Assert.Equal("boom", e.LastError);
    }

    [Fact]
    public void PaymentEvent_Lifecycle_PendingDispatchedProcessed()
    {
        var e = PaymentEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "T", "q", "{}");
        e.MarkDispatched(DateTimeOffset.UtcNow);
        Assert.Equal(OutboxStatus.Dispatched, e.Status);
        e.Requeue(DateTimeOffset.UtcNow);
        Assert.Equal(OutboxStatus.Pending, e.Status);
        e.MarkProcessed(DateTimeOffset.UtcNow);
        Assert.Equal(OutboxStatus.Processed, e.Status);
        Assert.NotNull(e.PublishedAt);
    }
}
