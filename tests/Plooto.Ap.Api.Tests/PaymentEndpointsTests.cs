using System.Net;
using System.Net.Http.Json;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Tests.TestSupport;

namespace Plooto.Ap.Api.Tests;

public class PaymentEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static async Task<PaymentResponse> ReadPaymentAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<PaymentResponse>())!;

    [Fact]
    public async Task CreatePayment_ValidInvoice_Returns201_AndFlowsToCompleted()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id, amount: 250m);

        var response = await s.PostPaymentAsync(invoice.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await ReadPaymentAsync(response);
        Assert.EndsWith($"/payments/{payment.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal(250m, payment.Amount);
        var detail = await s.WaitForPaymentStatusAsync(payment.Id, "Completed");
        Assert.Equal("Paid", detail.Invoice!.Status);
        Assert.Equal("Jane Doe", detail.Contractor!.Name);
        Assert.NotNull(detail.Payment.CompletedAt);
    }

    [Fact]
    public async Task CreatePayment_UnknownInvoice_Returns404_AndNothingIsCreated()
    {
        var s = await factory.CreateSessionAsync();
        var response = await s.PostPaymentAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task CreatePayment_InvoiceBelongingToAnotherCompany_Returns404()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var theirInvoice = await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id);

        var response = await mine.PostPaymentAsync(theirInvoice.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);   // indistinguishable from "does not exist"
    }

    [Fact]
    public async Task CreatePayment_AlreadyPaidInvoice_Returns409()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var first = await ReadPaymentAsync(await s.PostPaymentAsync(invoice.Id));
        await s.WaitForPaymentStatusAsync(first.Id, "Completed");

        var second = await s.PostPaymentAsync(invoice.Id);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_SecondPaymentWhileFirstInFlight_Returns409()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(invoice.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.PostPaymentAsync(invoice.Id)).StatusCode);
    }

    [Fact]
    public async Task RailDecline_FailsPayment_InvoiceStaysUnpaid_AndCanBeRetriedWithNewKey()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id, amount: 100.13m);   // .13 = declined by the simulator
        var payment = await ReadPaymentAsync(await s.PostPaymentAsync(invoice.Id));

        var failed = await s.WaitForPaymentStatusAsync(payment.Id, "Failed");

        Assert.Contains("Declined", failed.Payment.FailureReason);
        Assert.Equal("Approved", (await s.Client.GetFromJsonAsync<InvoiceResponse>(s.Path($"/invoices/{invoice.Id}")))!.Status);
        // A new idempotency key is a deliberate retry, and the invoice is free again.
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(invoice.Id)).StatusCode);
    }

    [Fact]
    public async Task GetPayment_Unknown_Returns404()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(s.Path($"/payments/{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task ListPayments_FiltersByStatusAndContractor()
    {
        var s = await factory.CreateSessionAsync();
        var jane = await s.CreateContractorAsync("jane@example.com");
        var sam = await s.CreateContractorAsync("sam@example.com");
        var p1 = await ReadPaymentAsync(await s.PostPaymentAsync((await s.CreateInvoiceAsync(jane.Id, "INV-1")).Id));
        var p2 = await ReadPaymentAsync(await s.PostPaymentAsync((await s.CreateInvoiceAsync(sam.Id, "INV-2")).Id));
        await s.WaitForPaymentStatusAsync(p1.Id, "Completed");
        await s.WaitForPaymentStatusAsync(p2.Id, "Completed");

        var byContractor = await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path($"/payments?contractorId={sam.Id}"));
        Assert.Equal(p2.Id, Assert.Single(byContractor!).Id);
        Assert.Equal(2, (await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments?status=completed")))!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync(s.Path("/payments?status=Bogus"))).StatusCode);
    }

    [Fact]
    public async Task Statement_ReflectsCompletedPayments_WithRunningBalance()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var p1 = await ReadPaymentAsync(await s.PostPaymentAsync((await s.CreateInvoiceAsync(c.Id, "INV-1", 100m)).Id));
        await s.WaitForPaymentStatusAsync(p1.Id, "Completed");
        var p2 = await ReadPaymentAsync(await s.PostPaymentAsync((await s.CreateInvoiceAsync(c.Id, "INV-2", 40.50m)).Id));
        await s.WaitForPaymentStatusAsync(p2.Id, "Completed");

        StatementResponse? statement = null;
        for (var i = 0; i < 200 && statement?.PaymentCount != 2; i++)   // projection is asynchronous
        {
            statement = await s.Client.GetFromJsonAsync<StatementResponse>(s.Path("/statement"));
            await Task.Delay(50);
        }

        Assert.Equal(2, statement!.PaymentCount);
        Assert.Equal(140.50m, statement.TotalPaid);
        Assert.Equal(-100m, statement.Lines[0].RunningBalance);
        Assert.Equal(-140.50m, statement.Lines[1].RunningBalance);
        Assert.All(statement.Lines, l => Assert.Equal("Jane Doe", l.CounterParty));
    }

    [Fact]
    public async Task Statement_DateRangeOutsideActivity_IsEmpty()
    {
        var s = await factory.CreateSessionAsync();
        var statement = await s.Client.GetFromJsonAsync<StatementResponse>(s.Path("/statement?from=2000-01-01T00:00:00Z&to=2000-12-31T00:00:00Z"));
        Assert.Empty(statement!.Lines);
        Assert.Equal(0m, statement.TotalPaid);
    }
}

public class ApprovalEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static async Task RequireApprovalAboveAsync(ApiSession s, decimal threshold)
    {
        var r = await s.Client.PutAsJsonAsync(s.Path("/preferences"), new UpdateCompanyPreferencesRequest("USD", true, threshold, "ACH"));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<(PaymentResponse Payment, ApprovalResponse Approval)> CreateGatedPaymentAsync(ApiSession s, decimal amount = 900m)
    {
        await RequireApprovalAboveAsync(s, 100m);
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id, amount: amount);
        var response = await s.PostPaymentAsync(invoice.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
        var approvals = (await s.Client.GetFromJsonAsync<List<ApprovalResponse>>(s.Path("/approvals?status=Pending")))!;
        return (payment, Assert.Single(approvals));
    }

    [Fact]
    public async Task Preferences_RoundTrip_AndRejectInvalid()
    {
        var s = await factory.CreateSessionAsync();
        await RequireApprovalAboveAsync(s, 250m);
        var prefs = (await s.Client.GetFromJsonAsync<CompanyPreferencesResponse>(s.Path("/preferences")))!;
        Assert.True(prefs.RequireApprovalAboveThreshold);
        Assert.Equal(250m, prefs.ApprovalThreshold);
        var bad = await s.Client.PutAsJsonAsync(s.Path("/preferences"), new UpdateCompanyPreferencesRequest("USD", true, -1m, "ACH"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PaymentAboveThreshold_StaysPending_UntilApproverApproves_ThenCompletes()
    {
        var s = await factory.CreateSessionAsync();
        var (payment, approval) = await CreateGatedPaymentAsync(s);
        Assert.Equal("Pending", payment.Status);
        Assert.Equal(payment.Id, approval.PaymentId);
        Assert.Equal("Pending", (await s.GetPaymentAsync(payment.Id)).ApprovalStatus);

        var decision = await s.Client.PostAsJsonAsync(s.Path($"/approvals/{approval.Id}/decision"),
            new ResolveApprovalRequest("Approve", "boss@example.com"));

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        await s.WaitForPaymentStatusAsync(payment.Id, "Completed");
        Assert.Equal("Approved", (await s.GetPaymentAsync(payment.Id)).ApprovalStatus);
    }

    [Fact]
    public async Task Rejection_CancelsPayment_InvoiceStaysSubmitted_AndCanBeResubmitted()
    {
        var s = await factory.CreateSessionAsync();
        var (payment, approval) = await CreateGatedPaymentAsync(s);

        var decision = await s.Client.PostAsJsonAsync(s.Path($"/approvals/{approval.Id}/decision"),
            new ResolveApprovalRequest("Reject", "boss@example.com"));

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var detail = await s.GetPaymentAsync(payment.Id);
        Assert.Equal("Cancelled", detail.Payment.Status);
        Assert.Equal("Submitted", detail.Invoice!.Status);
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(payment.InvoiceId)).StatusCode);   // new key, new attempt
    }

    [Fact]
    public async Task Decision_IsOneShot_SecondDecisionReturns409()
    {
        var s = await factory.CreateSessionAsync();
        var (_, approval) = await CreateGatedPaymentAsync(s);
        var path = s.Path($"/approvals/{approval.Id}/decision");
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync(path, new ResolveApprovalRequest("Reject", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(path, new ResolveApprovalRequest("Approve", null))).StatusCode);
    }

    [Fact]
    public async Task Decision_InvalidValue_Returns400_UnknownApproval_Returns404()
    {
        var s = await factory.CreateSessionAsync();
        var bad = await s.Client.PostAsJsonAsync(s.Path($"/approvals/{Guid.NewGuid()}/decision"), new ResolveApprovalRequest("Maybe", null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await s.Client.PostAsJsonAsync(s.Path($"/approvals/{Guid.NewGuid()}/decision"), new ResolveApprovalRequest("Approve", null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
