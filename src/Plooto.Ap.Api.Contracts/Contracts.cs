namespace Plooto.Ap.Api.Contracts;

// Request/response shapes shared by the API and Client.Sdk so the two cannot drift (PLAN_1 sections 4 and 8.1).
// Enums travel as strings. EF entities are never exposed.

public static class ApiRoutes
{
    public const string Companies = "/api/v1/companies";
    public static string Company(Guid id) => $"{Companies}/{id}";
}

public static class HeaderNames
{
    public const string IdempotencyKey = "Idempotency-Key";
}

// ---- Requests ---------------------------------------------------------------------------------------------------

public sealed record CreateCompanyRequest(string Name);

public sealed record UpdateCompanyPreferencesRequest(string DefaultCurrency, bool RequireApprovalAboveThreshold,
    decimal ApprovalThreshold, string PreferredPaymentRail);

public sealed record CreateContractorRequest(string Name, string Email, string PayoutAccountRef);
public sealed record UpdateContractorRequest(string Name, string Email, string PayoutAccountRef);

public sealed record CreateInvoiceRequest(Guid ContractorId, string InvoiceNumber, decimal Amount, string? Currency,
    DateOnly DueDate, string? Description);
public sealed record UpdateInvoiceRequest(decimal Amount, string? Currency, DateOnly DueDate, string? Description);

/// <summary>Marks request bodies covered by the Idempotency-Key contract.</summary>
public interface IIdempotentRequest;

public sealed record CreatePaymentRequest(Guid InvoiceId) : IIdempotentRequest;
public sealed record BatchPaymentItem(Guid InvoiceId);
public sealed record CreateBatchPaymentRequest(IReadOnlyList<BatchPaymentItem> Items) : IIdempotentRequest;

/// <summary>Decision is "Approve" or "Reject".</summary>
public sealed record ResolveApprovalRequest(string Decision, string? ApproverEmail);

// ---- Responses --------------------------------------------------------------------------------------------------

/// <summary><see cref="ApiKey"/> is only populated on creation: it is shown once and cannot be retrieved again.</summary>
public sealed record CompanyResponse(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt, string? ApiKey);

/// <summary>"tb company prefs": per-company payment policy. Payments above the threshold wait for human approval.</summary>
public sealed record CompanyPreferencesResponse(string DefaultCurrency, bool RequireApprovalAboveThreshold, decimal ApprovalThreshold,
    string PreferredPaymentRail);

public sealed record ContractorResponse(Guid Id, Guid CompanyId, string Name, string Email, string PayoutAccountRef, bool IsActive);

public sealed record InvoiceResponse(Guid Id, Guid CompanyId, Guid ContractorId, string InvoiceNumber, decimal Amount,
    string Currency, string? Description, string Status, DateOnly DueDate, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);

public sealed record PaymentResponse(Guid Id, Guid CompanyId, Guid InvoiceId, Guid ContractorId, decimal Amount, string Currency,
    string Status, string Method, Guid? BatchId, string? Rail, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    string? FailureReason);

public sealed record PaymentDetailResponse(PaymentResponse Payment, InvoiceResponse? Invoice, ContractorResponse? Contractor,
    Guid? ApprovalId, string? ApprovalStatus);

public sealed record BatchPaymentResult(Guid InvoiceId, int StatusCode, string Outcome, Guid? PaymentId, string? Error);
public sealed record BatchPaymentResponse(Guid BatchId, IReadOnlyList<BatchPaymentResult> Results);

public sealed record ApprovalResponse(Guid Id, Guid CompanyId, Guid InvoiceId, Guid PaymentId, string Status, string? ApproverEmail,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

public sealed record StatementLineResponse(DateTimeOffset Date, string Type, string CounterParty, decimal Amount, string Currency,
    Guid PaymentId, decimal RunningBalance);
public sealed record StatementResponse(Guid CompanyId, DateTimeOffset? From, DateTimeOffset? To,
    IReadOnlyList<StatementLineResponse> Lines, decimal TotalPaid, int PaymentCount);

public sealed record ErrorResponse(string Error);
