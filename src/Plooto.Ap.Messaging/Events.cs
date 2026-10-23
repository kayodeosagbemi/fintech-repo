namespace Plooto.Ap.Messaging;

/// <summary>Named queues from the design diagram (PLAN_1 section 3.3.1).</summary>
public static class QueueNames
{
    public const string Notifications = "notifications";
    public const string ValidatedPayments = "validated-payments";
    public const string PaymentEventsCdc = "payment-events-cdc";
    public const string PymtQueue = "pymt-queue";

    public static readonly IReadOnlyList<string> All = new[] { Notifications, ValidatedPayments, PaymentEventsCdc, PymtQueue };
}

public abstract record IntegrationEvent(Guid PaymentId, Guid CompanyId);

/// <summary>A payment needs human sign-off.</summary>
public sealed record ApprovalRequested(Guid PaymentId, Guid CompanyId, Guid ApprovalId, Guid InvoiceId, decimal Amount, string Currency)
    : IntegrationEvent(PaymentId, CompanyId);

/// <summary>The payment is cleared for processing (validated and, if required, approved).</summary>
public sealed record PaymentValidated(Guid PaymentId, Guid CompanyId) : IntegrationEvent(PaymentId, CompanyId);

/// <summary>Status change the UI/approver should hear about (e.g. rejection).</summary>
public sealed record PaymentNotification(Guid PaymentId, Guid CompanyId, string Status, string Message)
    : IntegrationEvent(PaymentId, CompanyId);

public sealed record PaymentProcessed(Guid PaymentId, Guid CompanyId) : IntegrationEvent(PaymentId, CompanyId);

public sealed record PaymentFailed(Guid PaymentId, Guid CompanyId, string Reason) : IntegrationEvent(PaymentId, CompanyId);
