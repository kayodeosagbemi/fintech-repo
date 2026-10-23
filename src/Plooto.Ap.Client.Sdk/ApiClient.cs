using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plooto.Ap.Api.Contracts;

namespace Plooto.Ap.Client.Sdk;

/// <summary>Connection details shared by every front-end (console today, Blazor tomorrow). Held in memory only.</summary>
public sealed class ClientSession
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiKey { get; set; }
    public Guid? CompanyId { get; set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(ApiKey) && CompanyId is not null;
}

public sealed class ApiClientOptions
{
    /// <summary>Total attempts for a retryable request (first try included).</summary>
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>Outcome of one API call. Failures are values, not exceptions, so UIs can render them.</summary>
public sealed record ApiResponse<T>(int StatusCode, T? Value, string? Error)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Thin HttpClient wrapper: one method per API endpoint (PLAN_1 section 8.1). Transient failures (network errors, timeouts,
/// 5xx) are retried with exponential backoff; 4xx is never retried because it won't change on its own.
/// A POST without an Idempotency-Key is never retried either, since a lost response could otherwise create a duplicate.
/// </summary>
public sealed class ApiClient(HttpClient http, ClientSession session, ApiClientOptions? options = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiClientOptions _options = options ?? new ApiClientOptions();

    // ---- Companies ---------------------------------------------------------------------------------------------------
    public Task<ApiResponse<CompanyResponse>> CreateCompanyAsync(string name, CancellationToken ct = default) =>
        SendAsync<CompanyResponse>(HttpMethod.Post, ApiRoutes.Companies, new CreateCompanyRequest(name), authenticated: false, ct: ct);

    public Task<ApiResponse<CompanyResponse>> GetCompanyAsync(CancellationToken ct = default) =>
        SendAsync<CompanyResponse>(HttpMethod.Get, CompanyPath(""), ct: ct);

    public Task<ApiResponse<CompanyPreferencesResponse>> GetPreferencesAsync(CancellationToken ct = default) =>
        SendAsync<CompanyPreferencesResponse>(HttpMethod.Get, CompanyPath("/preferences"), ct: ct);

    public Task<ApiResponse<CompanyPreferencesResponse>> UpdatePreferencesAsync(UpdateCompanyPreferencesRequest request, CancellationToken ct = default) =>
        SendAsync<CompanyPreferencesResponse>(HttpMethod.Put, CompanyPath("/preferences"), request, ct: ct);

    // ---- Contractors -------------------------------------------------------------------------------------------------
    public Task<ApiResponse<ContractorResponse>> CreateContractorAsync(CreateContractorRequest request, CancellationToken ct = default) =>
        SendAsync<ContractorResponse>(HttpMethod.Post, CompanyPath("/contractors"), request, ct: ct);

    public Task<ApiResponse<List<ContractorResponse>>> ListContractorsAsync(string? email = null, int? page = null, int? pageSize = null, CancellationToken ct = default) =>
        SendAsync<List<ContractorResponse>>(HttpMethod.Get,
            CompanyPath("/contractors" + Query(("email", email), ("page", page?.ToString()), ("pageSize", pageSize?.ToString()))), ct: ct);

    // ---- Invoices ----------------------------------------------------------------------------------------------------
    public Task<ApiResponse<InvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) =>
        SendAsync<InvoiceResponse>(HttpMethod.Post, CompanyPath("/invoices"), request, ct: ct);

    public Task<ApiResponse<List<InvoiceResponse>>> ListInvoicesAsync(string? status = null, Guid? contractorId = null, CancellationToken ct = default) =>
        SendAsync<List<InvoiceResponse>>(HttpMethod.Get,
            CompanyPath("/invoices" + Query(("status", status), ("contractorId", contractorId?.ToString()))), ct: ct);

    // ---- Payments ----------------------------------------------------------------------------------------------------
    /// <summary>Safe to retry with the SAME key; use a NEW key for a deliberate new attempt.</summary>
    public Task<ApiResponse<PaymentResponse>> CreatePaymentAsync(Guid invoiceId, string idempotencyKey, CancellationToken ct = default) =>
        SendAsync<PaymentResponse>(HttpMethod.Post, CompanyPath("/payments"), new CreatePaymentRequest(invoiceId), idempotencyKey: idempotencyKey, ct: ct);

    public Task<ApiResponse<BatchPaymentResponse>> CreateBatchPaymentAsync(IEnumerable<Guid> invoiceIds, string idempotencyKey, CancellationToken ct = default) =>
        SendAsync<BatchPaymentResponse>(HttpMethod.Post, CompanyPath("/payments/batch"),
            new CreateBatchPaymentRequest(invoiceIds.Select(i => new BatchPaymentItem(i)).ToList()), idempotencyKey: idempotencyKey, ct: ct);

    public Task<ApiResponse<PaymentDetailResponse>> GetPaymentDetailsAsync(Guid paymentId, CancellationToken ct = default) =>
        SendAsync<PaymentDetailResponse>(HttpMethod.Get, CompanyPath($"/payments/{paymentId}"), ct: ct);

    public Task<ApiResponse<List<PaymentResponse>>> ListPaymentsAsync(string? status = null, Guid? batchId = null, CancellationToken ct = default) =>
        SendAsync<List<PaymentResponse>>(HttpMethod.Get,
            CompanyPath("/payments" + Query(("status", status), ("batchId", batchId?.ToString()))), ct: ct);

    // ---- Approvals & statement ---------------------------------------------------------------------------------------
    public Task<ApiResponse<List<ApprovalResponse>>> ListApprovalsAsync(string? status = null, CancellationToken ct = default) =>
        SendAsync<List<ApprovalResponse>>(HttpMethod.Get, CompanyPath("/approvals" + Query(("status", status))), ct: ct);

    public Task<ApiResponse<PaymentResponse>> ResolveApprovalAsync(Guid approvalId, string decision, string? approverEmail, CancellationToken ct = default) =>
        SendAsync<PaymentResponse>(HttpMethod.Post, CompanyPath($"/approvals/{approvalId}/decision"),
            new ResolveApprovalRequest(decision, approverEmail), ct: ct);

    public Task<ApiResponse<StatementResponse>> GetStatementAsync(DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        SendAsync<StatementResponse>(HttpMethod.Get,
            CompanyPath("/statement" + Query(("from", from?.ToString("o")), ("to", to?.ToString("o")))), ct: ct);

    // ---- Plumbing ----------------------------------------------------------------------------------------------------
    private string CompanyPath(string suffix)
    {
        if (session.CompanyId is not { } id)
            throw new InvalidOperationException("No company is configured. Create a company or enter an existing company id and API key first.");
        return ApiRoutes.Company(id) + suffix;
    }

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    private async Task<ApiResponse<T>> SendAsync<T>(HttpMethod method, string path, object? body = null,
        string? idempotencyKey = null, bool authenticated = true, CancellationToken ct = default)
    {
        var retryable = method != HttpMethod.Post || idempotencyKey is not null;
        var attempts = retryable ? Math.Max(1, _options.MaxAttempts) : 1;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = BuildRequest(method, path, body, idempotencyKey, authenticated);
                using var response = await http.SendAsync(request, ct);
                if ((int)response.StatusCode >= 500 && attempt < attempts)
                {
                    await BackoffAsync(attempt, ct);
                    continue;
                }
                return await ReadAsync<T>(response, ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < attempts)
            {
                await BackoffAsync(attempt, ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return new ApiResponse<T>(0, default, $"Could not reach the API at {session.BaseUrl}: {ex.Message}");
            }
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body, string? idempotencyKey, bool authenticated)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        if (authenticated && !string.IsNullOrWhiteSpace(session.ApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.ApiKey);
        if (idempotencyKey is not null)
            request.Headers.Add(HeaderNames.IdempotencyKey, idempotencyKey);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return request;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or TimeoutException;

    private Task BackoffAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(_options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)), ct);

    private static async Task<ApiResponse<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var value = response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return new ApiResponse<T>(status, value, null);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        string? error = null;
        try { error = JsonSerializer.Deserialize<ErrorResponse>(text, Json)?.Error; }
        catch (JsonException) { /* not our error shape (e.g. a proxy page) */ }
        error ??= string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase ?? $"HTTP {status}" : text;
        return new ApiResponse<T>(status, default, error);
    }
}
