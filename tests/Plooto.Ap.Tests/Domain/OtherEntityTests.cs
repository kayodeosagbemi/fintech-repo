using Plooto.Ap.Domain.Common;
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
        Assert.Throws<DomainRuleException>(() => a.Resolve(ApprovalDecision.Reject, null));
    }

    [Fact]
    public void ApprovalRequest_Resolve_RecordsApproverAndTimestamp()
    {
        var a = ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        a.Resolve(ApprovalDecision.Approve, "boss@example.com");
        Assert.Equal("boss@example.com", a.ApproverEmail);
        Assert.NotNull(a.ResolvedAt);
    }

    [Fact]
    public void CompanyPreferences_NegativeThreshold_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanyPreferences.Create(Guid.NewGuid(), "USD", true, -1m, "ACH"));

    [Fact]
    public void CompanyPreferences_BadCurrencyOrRail_Throws()
    {
        Assert.Throws<ArgumentException>(() => CompanyPreferences.Create(Guid.NewGuid(), "US", true, 1m, "ACH"));
        Assert.Throws<ArgumentException>(() => CompanyPreferences.Create(Guid.NewGuid(), "USD", true, 1m, " "));
    }

    [Fact]
    public void IdempotencyRecord_Begin_BlankKeyOrHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyRecord.Begin(Guid.NewGuid(), " ", "h"));
        Assert.Throws<ArgumentException>(() => IdempotencyRecord.Begin(Guid.NewGuid(), "k", ""));
    }

    [Fact]
    public void IdempotencyRecord_Complete_NullBody_BecomesEmpty()
    {
        var r = IdempotencyRecord.Begin(Guid.NewGuid(), "k", "h");
        r.Complete(204, null);
        Assert.Equal("", r.ResponseBody);
    }

    [Fact]
    public void PaymentEvent_IllegalTransitions_Throw()
    {
        var now = DateTimeOffset.UtcNow;
        var e = PaymentEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "T", "q", "{}");
        Assert.Throws<DomainRuleException>(() => e.MarkProcessed(now));
        Assert.Throws<DomainRuleException>(() => e.Requeue(now));
        e.MarkDispatched(now);
        Assert.Throws<DomainRuleException>(() => e.MarkDispatched(now));
        e.MarkProcessed(now);
        Assert.Throws<DomainRuleException>(() => e.MarkProcessed(now));
        Assert.Throws<DomainRuleException>(() => e.Requeue(now));
        Assert.Throws<DomainRuleException>(() => e.RecordFailure("x", now));
    }

    [Fact]
    public void PaymentEvent_DeadLettered_CannotBeResurrected()
    {
        var now = DateTimeOffset.UtcNow;
        var e = PaymentEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "T", "q", "{}");
        for (var i = 0; i < PaymentEvent.MaxAttempts; i++) e.RecordFailure("boom", now);
        Assert.Equal(OutboxStatus.DeadLettered, e.Status);
        Assert.Throws<DomainRuleException>(() => e.MarkDispatched(now));
        Assert.Throws<DomainRuleException>(() => e.MarkProcessed(now));
        Assert.Throws<DomainRuleException>(() => e.Requeue(now));
        Assert.Throws<DomainRuleException>(() => e.RecordFailure("x", now));
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
        Assert.Throws<DomainRuleException>(() => r.Complete(200, "{}"));
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
        e.MarkDispatched(DateTimeOffset.UtcNow);
        e.MarkProcessed(DateTimeOffset.UtcNow);
        Assert.Equal(OutboxStatus.Processed, e.Status);
        Assert.NotNull(e.PublishedAt);
    }
}
