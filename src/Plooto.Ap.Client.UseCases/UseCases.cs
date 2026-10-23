using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Client.Sdk;

namespace Plooto.Ap.Client.UseCases;

// One class per console menu option (PLAN_1 section 6.2). They depend only on Client.Sdk: never on System.Console and never
// on file paths, so a Blazor page can call exactly the same methods (section 8).

public sealed record UseCaseResult<T>(bool Success, T? Value, string? Error, int StatusCode = 0)
{
    public static UseCaseResult<T> From(ApiResponse<T> r) => new(r.IsSuccess, r.Value, r.Error, r.StatusCode);
    public static UseCaseResult<T> Fail(string error) => new(false, default, error);
    public static UseCaseResult<T> Ok(T value) => new(true, value, null, 200);
}

// ---- Setup -------------------------------------------------------------------------------------------------------------

public interface IConfigureConnectionUseCase
{
    /// <summary>
    /// Stores the connection details for this session. If only an API key is given, the company id is discovered from the
    /// API itself (GET /api/v1/me), so a user never has to look it up.
    /// </summary>
    Task<UseCaseResult<ClientSession>> ExecuteAsync(string baseUrl, Guid? companyId, string? apiKey, CancellationToken ct = default);
}

public sealed class ConfigureConnectionUseCase(ClientSession session, ApiClient api) : IConfigureConnectionUseCase
{
    public async Task<UseCaseResult<ClientSession>> ExecuteAsync(string baseUrl, Guid? companyId, string? apiKey, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return UseCaseResult<ClientSession>.Fail($"'{baseUrl}' is not a valid http(s) URL.");
        session.BaseUrl = baseUrl.TrimEnd('/');
        session.ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        session.CompanyId = companyId;

        if (session.ApiKey is not null && companyId is null)
        {
            var me = await api.GetCurrentCompanyAsync(ct);
            if (!me.IsSuccess)
                return UseCaseResult<ClientSession>.Fail($"Could not resolve the company for that API key: {me.Error}");
            session.CompanyId = me.Value!.Id;
        }
        return UseCaseResult<ClientSession>.Ok(session);
    }
}

public interface ICreateCompanyUseCase
{
    Task<UseCaseResult<CompanyResponse>> ExecuteAsync(string name, CancellationToken ct = default);
}

/// <summary>Bootstrap: creates a company and adopts the returned (one-time) API key as the session's credentials.</summary>
public sealed class CreateCompanyUseCase(ApiClient api, ClientSession session) : ICreateCompanyUseCase
{
    public async Task<UseCaseResult<CompanyResponse>> ExecuteAsync(string name, CancellationToken ct = default)
    {
        var response = await api.CreateCompanyAsync(name, ct);
        if (response.IsSuccess)
        {
            session.CompanyId = response.Value!.Id;
            session.ApiKey = response.Value.ApiKey;
        }
        return UseCaseResult<CompanyResponse>.From(response);
    }
}

public interface ISetApprovalPolicyUseCase
{
    Task<UseCaseResult<CompanyPreferencesResponse>> ExecuteAsync(bool requireApproval, decimal threshold, CancellationToken ct = default);
    Task<UseCaseResult<CompanyPreferencesResponse>> GetAsync(CancellationToken ct = default);
}

public sealed class SetApprovalPolicyUseCase(ApiClient api) : ISetApprovalPolicyUseCase
{
    public async Task<UseCaseResult<CompanyPreferencesResponse>> ExecuteAsync(bool requireApproval, decimal threshold, CancellationToken ct = default)
    {
        var current = await api.GetPreferencesAsync(ct);
        if (!current.IsSuccess) return UseCaseResult<CompanyPreferencesResponse>.From(current);
        var p = current.Value!;
        return UseCaseResult<CompanyPreferencesResponse>.From(await api.UpdatePreferencesAsync(
            new UpdateCompanyPreferencesRequest(p.DefaultCurrency, requireApproval, threshold, p.PreferredPaymentRail), ct));
    }

    public async Task<UseCaseResult<CompanyPreferencesResponse>> GetAsync(CancellationToken ct = default) =>
        UseCaseResult<CompanyPreferencesResponse>.From(await api.GetPreferencesAsync(ct));
}

// ---- Contractors & invoices --------------------------------------------------------------------------------------------

public interface ICreateContractorUseCase
{
    Task<UseCaseResult<ContractorResponse>> ExecuteAsync(string name, string email, string payoutAccountRef, CancellationToken ct = default);
}

public sealed class CreateContractorUseCase(ApiClient api) : ICreateContractorUseCase
{
    public async Task<UseCaseResult<ContractorResponse>> ExecuteAsync(string name, string email, string payoutAccountRef, CancellationToken ct = default) =>
        UseCaseResult<ContractorResponse>.From(await api.CreateContractorAsync(new CreateContractorRequest(name, email, payoutAccountRef), ct));
}

public interface IListContractorsUseCase
{
    Task<UseCaseResult<List<ContractorResponse>>> ExecuteAsync(CancellationToken ct = default);
}

public sealed class ListContractorsUseCase(ApiClient api) : IListContractorsUseCase
{
    public async Task<UseCaseResult<List<ContractorResponse>>> ExecuteAsync(CancellationToken ct = default) =>
        UseCaseResult<List<ContractorResponse>>.From(await api.ListContractorsAsync(ct: ct));
}

public interface ICreateInvoiceUseCase
{
    /// <summary>The contractor is identified by email, like the CSV import does.</summary>
    Task<UseCaseResult<InvoiceResponse>> ExecuteAsync(string contractorEmail, string invoiceNumber, decimal amount, string? currency,
        DateOnly dueDate, string? description, CancellationToken ct = default);
}

public sealed class CreateInvoiceUseCase(ApiClient api) : ICreateInvoiceUseCase
{
    public async Task<UseCaseResult<InvoiceResponse>> ExecuteAsync(string contractorEmail, string invoiceNumber, decimal amount,
        string? currency, DateOnly dueDate, string? description, CancellationToken ct = default)
    {
        var contractors = await api.ListContractorsAsync(contractorEmail, ct: ct);
        if (!contractors.IsSuccess) return UseCaseResult<InvoiceResponse>.Fail(contractors.Error ?? "Could not look up the contractor.");
        var contractor = contractors.Value!.FirstOrDefault();
        if (contractor is null) return UseCaseResult<InvoiceResponse>.Fail($"No active contractor with email '{contractorEmail}'. Create the contractor first.");

        return UseCaseResult<InvoiceResponse>.From(await api.CreateInvoiceAsync(
            new CreateInvoiceRequest(contractor.Id, invoiceNumber, amount, string.IsNullOrWhiteSpace(currency) ? null : currency, dueDate, description), ct));
    }
}

public interface IListInvoicesUseCase
{
    Task<UseCaseResult<List<InvoiceResponse>>> ExecuteAsync(string? status = null, CancellationToken ct = default);
}

public sealed class ListInvoicesUseCase(ApiClient api) : IListInvoicesUseCase
{
    public async Task<UseCaseResult<List<InvoiceResponse>>> ExecuteAsync(string? status = null, CancellationToken ct = default) =>
        UseCaseResult<List<InvoiceResponse>>.From(await api.ListInvoicesAsync(status, ct: ct));
}

// ---- CSV ---------------------------------------------------------------------------------------------------------------

public sealed record ImportedInvoice(int LineNumber, string InvoiceNumber, Guid InvoiceId);
public sealed record CsvImportResult(IReadOnlyList<ImportedInvoice> Created, IReadOnlyList<CsvRowError> Errors, IReadOnlyList<CsvRowWarning> Warnings);

public interface IImportInvoicesFromCsvUseCase
{
    /// <summary>Stream-based so a Blazor InputFile can supply it directly (PLAN_1 section 8.1).</summary>
    Task<CsvImportResult> ExecuteAsync(Stream csvStream, CancellationToken ct = default);
}

/// <summary>
/// Partial success by design: each row is independent. Parse failures, unknown contractor emails and API rejections
/// (e.g. duplicate invoice number) become per-row errors with the line number; valid rows are still created.
/// </summary>
public sealed class ImportInvoicesFromCsvUseCase(ApiClient api, InvoiceCsvService csv) : IImportInvoicesFromCsvUseCase
{
    public async Task<CsvImportResult> ExecuteAsync(Stream csvStream, CancellationToken ct = default)
    {
        var parsed = await csv.ParseAsync(csvStream, ct);
        var created = new List<ImportedInvoice>();
        var errors = new List<CsvRowError>(parsed.Errors);
        var contractorIds = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsed.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!contractorIds.TryGetValue(row.ContractorEmail, out var contractorId))
            {
                var lookup = await api.ListContractorsAsync(row.ContractorEmail, ct: ct);
                if (!lookup.IsSuccess)
                {
                    errors.Add(new CsvRowError(row.LineNumber, row.InvoiceNumber, $"Contractor lookup failed: {lookup.Error}"));
                    continue;
                }
                contractorId = lookup.Value!.FirstOrDefault()?.Id;
                contractorIds[row.ContractorEmail] = contractorId;
            }
            if (contractorId is null)
            {
                errors.Add(new CsvRowError(row.LineNumber, row.InvoiceNumber, $"No active contractor with email '{row.ContractorEmail}'."));
                continue;
            }

            var response = await api.CreateInvoiceAsync(new CreateInvoiceRequest(contractorId.Value, row.InvoiceNumber, row.Amount,
                row.Currency, row.DueDate, row.Description), ct);
            if (response.IsSuccess) created.Add(new ImportedInvoice(row.LineNumber, row.InvoiceNumber, response.Value!.Id));
            else errors.Add(new CsvRowError(row.LineNumber, row.InvoiceNumber, response.Error ?? $"API returned {response.StatusCode}."));
        }

        return new CsvImportResult(created, errors.OrderBy(e => e.LineNumber).ToList(), parsed.Warnings);
    }
}

public interface IDownloadCsvTemplateUseCase
{
    /// <summary>Writes the template to any destination: a file today, a browser download tomorrow.</summary>
    Task ExecuteAsync(Stream destination, CancellationToken ct = default);
}

public sealed class DownloadCsvTemplateUseCase(InvoiceCsvService csv) : IDownloadCsvTemplateUseCase
{
    public Task ExecuteAsync(Stream destination, CancellationToken ct = default) => csv.WriteTemplateAsync(destination, ct);
}

// ---- Payments ----------------------------------------------------------------------------------------------------------

public interface ICreatePaymentUseCase
{
    /// <summary>Pass the same <paramref name="idempotencyKey"/> to retry safely; omit it for a new attempt.</summary>
    Task<UseCaseResult<PaymentResponse>> ExecuteAsync(Guid invoiceId, string? idempotencyKey = null, CancellationToken ct = default);
}

public sealed class CreatePaymentUseCase(ApiClient api) : ICreatePaymentUseCase
{
    public async Task<UseCaseResult<PaymentResponse>> ExecuteAsync(Guid invoiceId, string? idempotencyKey = null, CancellationToken ct = default) =>
        UseCaseResult<PaymentResponse>.From(await api.CreatePaymentAsync(invoiceId, idempotencyKey ?? Guid.NewGuid().ToString(), ct));
}

public interface ICreateBatchPaymentUseCase
{
    Task<UseCaseResult<BatchPaymentResponse>> ExecuteAsync(IReadOnlyList<Guid> invoiceIds, string? idempotencyKey = null, CancellationToken ct = default);
}

public sealed class CreateBatchPaymentUseCase(ApiClient api) : ICreateBatchPaymentUseCase
{
    public async Task<UseCaseResult<BatchPaymentResponse>> ExecuteAsync(IReadOnlyList<Guid> invoiceIds, string? idempotencyKey = null, CancellationToken ct = default) =>
        UseCaseResult<BatchPaymentResponse>.From(await api.CreateBatchPaymentAsync(invoiceIds, idempotencyKey ?? Guid.NewGuid().ToString(), ct));
}

public interface IResolveApprovalUseCase
{
    Task<UseCaseResult<List<ApprovalResponse>>> ListPendingAsync(CancellationToken ct = default);
    Task<UseCaseResult<PaymentResponse>> ExecuteAsync(Guid approvalId, bool approve, string? approverEmail, CancellationToken ct = default);
}

public sealed class ResolveApprovalUseCase(ApiClient api) : IResolveApprovalUseCase
{
    public async Task<UseCaseResult<List<ApprovalResponse>>> ListPendingAsync(CancellationToken ct = default) =>
        UseCaseResult<List<ApprovalResponse>>.From(await api.ListApprovalsAsync("Pending", ct));

    public async Task<UseCaseResult<PaymentResponse>> ExecuteAsync(Guid approvalId, bool approve, string? approverEmail, CancellationToken ct = default) =>
        UseCaseResult<PaymentResponse>.From(await api.ResolveApprovalAsync(approvalId, approve ? "Approve" : "Reject", approverEmail, ct));
}

public interface IGetPaymentDetailsUseCase
{
    Task<UseCaseResult<PaymentDetailResponse>> ExecuteAsync(Guid paymentId, CancellationToken ct = default);
    Task<UseCaseResult<List<PaymentResponse>>> ListAsync(Guid? batchId = null, CancellationToken ct = default);
}

public sealed class GetPaymentDetailsUseCase(ApiClient api) : IGetPaymentDetailsUseCase
{
    public async Task<UseCaseResult<PaymentDetailResponse>> ExecuteAsync(Guid paymentId, CancellationToken ct = default) =>
        UseCaseResult<PaymentDetailResponse>.From(await api.GetPaymentDetailsAsync(paymentId, ct));

    public async Task<UseCaseResult<List<PaymentResponse>>> ListAsync(Guid? batchId = null, CancellationToken ct = default) =>
        UseCaseResult<List<PaymentResponse>>.From(await api.ListPaymentsAsync(batchId: batchId, ct: ct));
}

public interface IGetStatementUseCase
{
    Task<UseCaseResult<StatementResponse>> ExecuteAsync(DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default);
}

public sealed class GetStatementUseCase(ApiClient api) : IGetStatementUseCase
{
    public async Task<UseCaseResult<StatementResponse>> ExecuteAsync(DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        UseCaseResult<StatementResponse>.From(await api.GetStatementAsync(from, to, ct));
}

// ---- Composition -------------------------------------------------------------------------------------------------------

/// <summary>Everything a front-end needs, wired once. Console and (future) Blazor both construct this.</summary>
public sealed class ClientSuite
{
    public ClientSession Session { get; }
    public ApiClient Api { get; }
    public IConfigureConnectionUseCase Configure { get; }
    public ICreateCompanyUseCase CreateCompany { get; }
    public ISetApprovalPolicyUseCase ApprovalPolicy { get; }
    public ICreateContractorUseCase CreateContractor { get; }
    public IListContractorsUseCase ListContractors { get; }
    public ICreateInvoiceUseCase CreateInvoice { get; }
    public IListInvoicesUseCase ListInvoices { get; }
    public IImportInvoicesFromCsvUseCase ImportInvoices { get; }
    public IDownloadCsvTemplateUseCase DownloadTemplate { get; }
    public ICreatePaymentUseCase CreatePayment { get; }
    public ICreateBatchPaymentUseCase CreateBatchPayment { get; }
    public IResolveApprovalUseCase ResolveApproval { get; }
    public IGetPaymentDetailsUseCase PaymentDetails { get; }
    public IGetStatementUseCase Statement { get; }

    public ClientSuite(HttpClient http, ClientSession? session = null, ApiClientOptions? options = null)
    {
        Session = session ?? new ClientSession();
        Api = new ApiClient(http, Session, options);
        var csv = new InvoiceCsvService();
        Configure = new ConfigureConnectionUseCase(Session, Api);
        CreateCompany = new CreateCompanyUseCase(Api, Session);
        ApprovalPolicy = new SetApprovalPolicyUseCase(Api);
        CreateContractor = new CreateContractorUseCase(Api);
        ListContractors = new ListContractorsUseCase(Api);
        CreateInvoice = new CreateInvoiceUseCase(Api);
        ListInvoices = new ListInvoicesUseCase(Api);
        ImportInvoices = new ImportInvoicesFromCsvUseCase(Api, csv);
        DownloadTemplate = new DownloadCsvTemplateUseCase(csv);
        CreatePayment = new CreatePaymentUseCase(Api);
        CreateBatchPayment = new CreateBatchPaymentUseCase(Api);
        ResolveApproval = new ResolveApprovalUseCase(Api);
        PaymentDetails = new GetPaymentDetailsUseCase(Api);
        Statement = new GetStatementUseCase(Api);
    }
}
