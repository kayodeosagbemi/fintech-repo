using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Plooto.Ap.Api.Contracts;

namespace Plooto.Ap.Api.Tests.TestSupport;

/// <summary>The real API host over a throwaway SQLite file, with the background pipeline running at test speed.</summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ap-api-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");   // not Development: no dev seed data in test databases
        builder.UseSetting("ConnectionStrings:Ap", $"Data Source={_dbPath}");
        builder.UseSetting("Outbox:PollInterval", "00:00:00.050");
        builder.UseSetting("SimulatedRail:Delay", "00:00:00.010");
        builder.UseSetting("PaymentProcessor:RailTimeout", "00:00:00.500");
    }

    /// <summary>Creates a company through the public endpoint and returns a client authenticated as it.</summary>
    public async Task<ApiSession> CreateSessionAsync(string name = "Acme")
    {
        var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync(ApiRoutes.Companies, new CreateCompanyRequest(name));
        response.EnsureSuccessStatusCode();
        var company = (await response.Content.ReadFromJsonAsync<CompanyResponse>())!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", company.ApiKey);
        return new ApiSession(client, company.Id, company.ApiKey!);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
        }
    }
}

public sealed class ApiSession(HttpClient client, Guid companyId, string apiKey)
{
    public HttpClient Client { get; } = client;
    public Guid CompanyId { get; } = companyId;
    public string ApiKey { get; } = apiKey;

    public string Path(string suffix = "") => $"{ApiRoutes.Company(CompanyId)}{suffix}";

    public async Task<ContractorResponse> CreateContractorAsync(string email = "jane@example.com")
    {
        var r = await Client.PostAsJsonAsync(Path("/contractors"), new CreateContractorRequest("Jane Doe", email, "acct_jane"));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ContractorResponse>())!;
    }

    public async Task<InvoiceResponse> CreateInvoiceAsync(Guid contractorId, string number = "INV-1", decimal amount = 100m)
    {
        var r = await Client.PostAsJsonAsync(Path("/invoices"),
            new CreateInvoiceRequest(contractorId, number, amount, "USD", new DateOnly(2027, 1, 1), "test"));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<InvoiceResponse>())!;
    }

    public Task<HttpResponseMessage> PostPaymentAsync(Guid invoiceId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path("/payments"))
        {
            Content = JsonContent.Create(new CreatePaymentRequest(invoiceId)),
        };
        request.Headers.Add(HeaderNames.IdempotencyKey, key ?? Guid.NewGuid().ToString());
        return Client.SendAsync(request);
    }

    public async Task<PaymentDetailResponse> GetPaymentAsync(Guid paymentId) =>
        (await Client.GetFromJsonAsync<PaymentDetailResponse>(Path($"/payments/{paymentId}")))!;

    /// <summary>Polls the payment until it reaches <paramref name="status"/> (processing is asynchronous by design).</summary>
    public async Task<PaymentDetailResponse> WaitForPaymentStatusAsync(Guid paymentId, string status, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        PaymentDetailResponse detail;
        do
        {
            detail = await GetPaymentAsync(paymentId);
            if (detail.Payment.Status == status) return detail;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        throw new Xunit.Sdk.XunitException($"Payment {paymentId} stayed {detail.Payment.Status}, expected {status}.");
    }
}
