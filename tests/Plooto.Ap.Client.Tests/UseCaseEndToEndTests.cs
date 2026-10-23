using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Plooto.Ap.Client.Sdk;
using Plooto.Ap.Client.UseCases;

namespace Plooto.Ap.Client.Tests;

/// <summary>The real use cases against the real API (in-process, throwaway SQLite): proves the client/server contract end to end.</summary>
public sealed class ClientApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ap-client-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ap", $"Data Source={_dbPath}");
        builder.UseSetting("Outbox:PollInterval", "00:00:00.050");
        builder.UseSetting("SimulatedRail:Delay", "00:00:00.010");
    }

    public ClientSuite NewSuite() => new(CreateClient(), new ClientSession { BaseUrl = "http://localhost" },
        new ApiClientOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });

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

public class UseCaseEndToEndTests(ClientApiFactory factory) : IClassFixture<ClientApiFactory>
{
    private static MemoryStream Csv(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

    private static async Task<ClientSuite> SignedUpSuiteAsync(ClientApiFactory factory, string name = "Acme")
    {
        var suite = factory.NewSuite();
        var created = await suite.CreateCompany.ExecuteAsync(name);
        Assert.True(created.Success, created.Error);
        Assert.True(suite.Session.IsAuthenticated);   // the use case adopts the one-time key
        return suite;
    }

    private static async Task<string> WaitForStatusAsync(ClientSuite suite, Guid paymentId, string status)
    {
        for (var i = 0; i < 300; i++)
        {
            var d = await suite.PaymentDetails.ExecuteAsync(paymentId);
            if (d.Value?.Payment.Status == status) return status;
            await Task.Delay(50);
        }
        return (await suite.PaymentDetails.ExecuteAsync(paymentId)).Value!.Payment.Status;
    }

    [Fact]
    public async Task CsvImport_MixedFile_CreatesValidRows_AndReportsEveryBadRowWithItsLineNumber()
    {
        var suite = await SignedUpSuiteAsync(factory);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        await suite.CreateContractor.ExecuteAsync("Sam", "sam@example.com", "acct_s");
        var first = await suite.ImportInvoices.ExecuteAsync(Csv(
            "InvoiceNumber,ContractorEmail,Amount,Currency,DueDate,Description", "EXISTING-1,jane@example.com,5,USD,2099-01-01,seed"));
        Assert.Single(first.Created);

        var result = await suite.ImportInvoices.ExecuteAsync(Csv(
            "InvoiceNumber,ContractorEmail,Amount,Currency,DueDate,Description",   // line 1
            "# example",                                                            // line 2
            "INV-1,jane@example.com,100,USD,2099-01-01,\"Design, build\"",          // line 3  ok
            "INV-2,nobody@example.com,100,USD,2099-01-01,unknown contractor",       // line 4  unknown email
            "EXISTING-1,sam@example.com,100,USD,2099-01-01,dup of existing",       // line 5  API 409
            "INV-4,sam@example.com,abc,USD,2099-01-01,bad amount",                  // line 6  parse error
            "INV-5,sam@example.com,50,,2001-01-01,default currency, past due"));    // line 7  ok (+ warning)

        Assert.Equal([3, 7], result.Created.Select(c => c.LineNumber));
        Assert.Equal([4, 5, 6], result.Errors.Select(e => e.LineNumber));
        Assert.Contains("nobody@example.com", result.Errors[0].Reason);
        Assert.Contains("already exists", result.Errors[1].Reason);
        Assert.Contains("not a number", result.Errors[2].Reason);
        Assert.Equal(7, Assert.Single(result.Warnings).LineNumber);

        var invoices = await suite.ListInvoices.ExecuteAsync();
        Assert.Equal(3, invoices.Value!.Count);   // EXISTING-1 + the two good rows
        Assert.Contains(invoices.Value, i => i.InvoiceNumber == "INV-5" && i.Currency == "USD");
    }

    [Fact]
    public async Task Template_Download_ThenImport_IsHarmless()
    {
        var suite = await SignedUpSuiteAsync(factory);
        using var ms = new MemoryStream();
        await suite.DownloadTemplate.ExecuteAsync(ms);
        ms.Position = 0;

        var result = await suite.ImportInvoices.ExecuteAsync(ms);

        Assert.Empty(result.Created);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PaymentFlow_CreateInvoiceByEmail_Pay_WatchItComplete_ThenStatement()
    {
        var suite = await SignedUpSuiteAsync(factory);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        var invoice = await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-1", 250m, null, new DateOnly(2099, 1, 1), "work");
        Assert.True(invoice.Success, invoice.Error);

        var payment = await suite.CreatePayment.ExecuteAsync(invoice.Value!.Id);

        Assert.True(payment.Success, payment.Error);
        Assert.Equal(201, payment.StatusCode);
        Assert.Equal("Completed", await WaitForStatusAsync(suite, payment.Value!.Id, "Completed"));
        StatementPaymentCount(await WaitForStatementAsync(suite, 1), 250m);
    }

    private static async Task<Api.Contracts.StatementResponse> WaitForStatementAsync(ClientSuite suite, int payments)
    {
        Api.Contracts.StatementResponse? s = null;
        for (var i = 0; i < 300 && s?.PaymentCount != payments; i++)
        {
            s = (await suite.Statement.ExecuteAsync()).Value;
            await Task.Delay(50);
        }
        return s!;
    }

    private static void StatementPaymentCount(Api.Contracts.StatementResponse s, decimal total)
    {
        Assert.Equal(total, s.TotalPaid);
        Assert.Equal(-total, s.Lines[^1].RunningBalance);
    }

    [Fact]
    public async Task SameIdempotencyKey_ReplaysInsteadOfPayingTwice()
    {
        var suite = await SignedUpSuiteAsync(factory);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        var invoice = (await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-1", 10m, null, new DateOnly(2099, 1, 1), null)).Value!;

        var a = await suite.CreatePayment.ExecuteAsync(invoice.Id, "retry-me");
        var b = await suite.CreatePayment.ExecuteAsync(invoice.Id, "retry-me");

        Assert.Equal(a.Value!.Id, b.Value!.Id);
        Assert.Single((await suite.PaymentDetails.ListAsync()).Value!);
    }

    [Fact]
    public async Task BatchPayment_ReportsPerItemOutcomes()
    {
        var suite = await SignedUpSuiteAsync(factory);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        var a = (await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-1", 10m, null, new DateOnly(2099, 1, 1), null)).Value!;
        var b = (await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-2", 20m, null, new DateOnly(2099, 1, 1), null)).Value!;

        var r = await suite.CreateBatchPayment.ExecuteAsync([a.Id, b.Id, Guid.NewGuid()]);

        Assert.True(r.Success, r.Error);
        Assert.Equal([201, 201, 404], r.Value!.Results.Select(x => x.StatusCode));
        Assert.Equal(2, (await suite.PaymentDetails.ListAsync(r.Value.BatchId)).Value!.Count);
    }

    [Fact]
    public async Task ApprovalFlow_PolicyOn_PaymentWaits_ApproverApproves_PaymentCompletes()
    {
        var suite = await SignedUpSuiteAsync(factory);
        Assert.True((await suite.ApprovalPolicy.ExecuteAsync(true, 100m)).Success);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        var invoice = (await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-1", 500m, null, new DateOnly(2099, 1, 1), null)).Value!;
        var payment = (await suite.CreatePayment.ExecuteAsync(invoice.Id)).Value!;
        Assert.Equal("Pending", payment.Status);

        var pending = (await suite.ResolveApproval.ListPendingAsync()).Value!;
        var approved = await suite.ResolveApproval.ExecuteAsync(Assert.Single(pending).Id, approve: true, "boss@example.com");

        Assert.True(approved.Success, approved.Error);
        Assert.Equal("Completed", await WaitForStatusAsync(suite, payment.Id, "Completed"));
        Assert.Empty((await suite.ResolveApproval.ListPendingAsync()).Value!);
    }

    [Fact]
    public async Task Rejection_CancelsThePayment()
    {
        var suite = await SignedUpSuiteAsync(factory);
        await suite.ApprovalPolicy.ExecuteAsync(true, 1m);
        await suite.CreateContractor.ExecuteAsync("Jane", "jane@example.com", "acct_j");
        var invoice = (await suite.CreateInvoice.ExecuteAsync("jane@example.com", "INV-1", 500m, null, new DateOnly(2099, 1, 1), null)).Value!;
        var payment = (await suite.CreatePayment.ExecuteAsync(invoice.Id)).Value!;
        var approval = (await suite.ResolveApproval.ListPendingAsync()).Value!.Single();

        var r = await suite.ResolveApproval.ExecuteAsync(approval.Id, approve: false, null);

        Assert.Equal("Cancelled", r.Value!.Status);
        Assert.Equal("Cancelled", (await suite.PaymentDetails.ExecuteAsync(payment.Id)).Value!.Payment.Status);
    }

    [Fact]
    public async Task Failures_AreReturnedAsValues_NotExceptions()
    {
        var suite = await SignedUpSuiteAsync(factory);
        var unknownInvoice = await suite.CreatePayment.ExecuteAsync(Guid.NewGuid());
        Assert.False(unknownInvoice.Success);
        Assert.Equal(404, unknownInvoice.StatusCode);

        var noContractor = await suite.CreateInvoice.ExecuteAsync("ghost@example.com", "INV-1", 5m, null, new DateOnly(2099, 1, 1), null);
        Assert.False(noContractor.Success);
        Assert.Contains("ghost@example.com", noContractor.Error);

        var wrongKey = factory.NewSuite();
        wrongKey.Session.CompanyId = suite.Session.CompanyId;
        wrongKey.Session.ApiKey = "wrong";
        var denied = await wrongKey.ListInvoices.ExecuteAsync();
        Assert.Equal(401, denied.StatusCode);
    }

    [Fact]
    public async Task ConfigureConnection_ValidatesTheUrl()
    {
        var suite = factory.NewSuite();
        Assert.False(suite.Configure.Execute("not a url", null, null).Success);
        var ok = suite.Configure.Execute("https://api.example.com/", Guid.NewGuid(), " key ");
        Assert.True(ok.Success);
        Assert.Equal("https://api.example.com", suite.Session.BaseUrl);
        Assert.Equal("key", suite.Session.ApiKey);
        await Task.CompletedTask;
    }
}
