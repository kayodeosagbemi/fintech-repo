using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Tests.TestSupport;

namespace Plooto.Ap.Api.Tests;

public class AuthenticationTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task MissingToken_Returns401()
    {
        var s = await factory.CreateSessionAsync();
        var anonymous = factory.CreateClient();
        var r = await anonymous.GetAsync(s.Path("/invoices"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("Bearer", r.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        var s = await factory.CreateSessionAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(s.Path("/invoices"))).StatusCode);
    }

    [Fact]
    public async Task WrongAuthScheme_Returns401()
    {
        var s = await factory.CreateSessionAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", s.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(s.Path("/invoices"))).StatusCode);
    }

    [Fact]
    public async Task ValidToken_MatchingCompanyRoute_Succeeds()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(s.Path("/invoices"))).StatusCode);
    }

    [Fact]
    public async Task ValidToken_WrongCompanyRoute_Returns403()
    {
        var a = await factory.CreateSessionAsync("A");
        var b = await factory.CreateSessionAsync("B");
        var r = await a.Client.GetAsync(b.Path("/invoices"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task ValidToken_UnknownCompanyRoute_Returns403_NotLeakingWhichCompaniesExist()
    {
        var a = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.GetAsync(ApiRoutes.Company(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task CompanyCreationAndHealth_AreOpen()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync(ApiRoutes.Companies, new CreateCompanyRequest("Open Co"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task ApiKey_IsNotStoredOrReturnedAgain()
    {
        var s = await factory.CreateSessionAsync();
        var text = await s.Client.GetStringAsync(s.Path());
        Assert.DoesNotContain(s.ApiKey, text);
    }
}

public class DataIsolationTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task GetInvoice_BelongingToOtherCompany_Returns403_NoData()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var theirInvoice = await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id);

        var r = await mine.Client.GetAsync(theirs.Path($"/invoices/{theirInvoice.Id}"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.DoesNotContain(theirInvoice.InvoiceNumber, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherCompanysInvoiceId_OnMyOwnRoute_Returns404()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var theirInvoice = await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Client.GetAsync(mine.Path($"/invoices/{theirInvoice.Id}"))).StatusCode);
    }

    [Fact]
    public async Task ListInvoices_OnlyReturnsOwnCompanyInvoices()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var myInvoice = await mine.CreateInvoiceAsync((await mine.CreateContractorAsync()).Id, "MINE-1");
        await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id, "THEIRS-1");

        var list = await mine.Client.GetFromJsonAsync<List<InvoiceResponse>>(mine.Path("/invoices"));

        Assert.Equal(myInvoice.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task SameInvoiceNumber_InTwoCompanies_DoesNotCollide()
    {
        var a = await factory.CreateSessionAsync("A");
        var b = await factory.CreateSessionAsync("B");
        await a.CreateInvoiceAsync((await a.CreateContractorAsync()).Id, "SHARED-1");
        await b.CreateInvoiceAsync((await b.CreateContractorAsync()).Id, "SHARED-1");
    }

    [Fact]
    public async Task GetPayment_CrossCompanyById_Returns403()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var invoice = await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id);
        var payment = (await (await theirs.PostPaymentAsync(invoice.Id)).Content.ReadFromJsonAsync<PaymentResponse>())!;

        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.GetAsync(theirs.Path($"/payments/{payment.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Client.GetAsync(mine.Path($"/payments/{payment.Id}"))).StatusCode);
    }

    [Fact]
    public async Task WritesAndReadsAgainstOtherCompany_AreAllForbidden()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var their = (Func<string, string>)theirs.Path;

        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.PostAsJsonAsync(their("/contractors"),
            new CreateContractorRequest("X", "x@example.com", "acct"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.PostAsJsonAsync(their("/invoices"),
            new CreateInvoiceRequest(Guid.NewGuid(), "I", 1m, "USD", new DateOnly(2027, 1, 1), null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.PutAsJsonAsync(their("/preferences"),
            new UpdateCompanyPreferencesRequest("USD", false, 0m, "ACH"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.GetAsync(their("/statement"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.GetAsync(their("/approvals"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.PostAsJsonAsync(their($"/approvals/{Guid.NewGuid()}/decision"),
            new ResolveApprovalRequest("Approve", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.Client.GetAsync(their("/payments"))).StatusCode);
    }

    [Fact]
    public async Task Statement_OnlyContainsOwnPayments()
    {
        var mine = await factory.CreateSessionAsync("Mine");
        var theirs = await factory.CreateSessionAsync("Theirs");
        var invoice = await theirs.CreateInvoiceAsync((await theirs.CreateContractorAsync()).Id, amount: 77m);
        var payment = (await (await theirs.PostPaymentAsync(invoice.Id)).Content.ReadFromJsonAsync<PaymentResponse>())!;
        await theirs.WaitForPaymentStatusAsync(payment.Id, "Completed");
        // Wait until their statement projection has landed, so "mine is empty" cannot pass just because we looked too early.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await theirs.Client.GetFromJsonAsync<StatementResponse>(theirs.Path("/statement")))!.PaymentCount != 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "Their statement never reflected the payment.");
            await Task.Delay(50);
        }

        var statement = await mine.Client.GetFromJsonAsync<StatementResponse>(mine.Path("/statement"));
        Assert.Empty(statement!.Lines);
    }
}
