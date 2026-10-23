using System.Net;
using System.Net.Http.Json;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Tests.TestSupport;

namespace Plooto.Ap.Api.Tests;

public class CompanyEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task CreateCompany_ReturnsCreatedWithLocationAndOneTimeApiKey()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(ApiRoutes.Companies, new CreateCompanyRequest("Acme Inc"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = (await response.Content.ReadFromJsonAsync<CompanyResponse>())!;
        Assert.Equal($"{ApiRoutes.Company(company.Id)}", response.Headers.Location!.OriginalString);
        Assert.False(string.IsNullOrEmpty(company.ApiKey));
        Assert.StartsWith("acme-inc-", company.Slug);
    }

    [Fact]
    public async Task CreateCompany_BlankName_Returns400()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(ApiRoutes.Companies, new CreateCompanyRequest("  "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCompany_Existing_ReturnsItWithoutApiKey()
    {
        var s = await factory.CreateSessionAsync();
        var company = await s.Client.GetFromJsonAsync<CompanyResponse>(s.Path());
        Assert.Equal(s.CompanyId, company!.Id);
        Assert.Null(company.ApiKey);
    }
}

public class ContractorEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task Create_ValidRequest_Returns201WithLocation()
    {
        var s = await factory.CreateSessionAsync();
        var r = await s.Client.PostAsJsonAsync(s.Path("/contractors"), new CreateContractorRequest("Jane", "JANE@example.com", "acct_1"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var c = (await r.Content.ReadFromJsonAsync<ContractorResponse>())!;
        Assert.Equal("jane@example.com", c.Email);
        Assert.EndsWith($"/contractors/{c.Id}", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Create_InvalidEmail_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        var r = await s.Client.PostAsJsonAsync(s.Path("/contractors"), new CreateContractorRequest("Jane", "nope", "acct_1"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Get_Existing_Returns200_AndUnknown_Returns404()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(s.Path($"/contractors/{c.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(s.Path($"/contractors/{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task List_SupportsEmailFilterAndPaging()
    {
        var s = await factory.CreateSessionAsync();
        await s.CreateContractorAsync("a@example.com");
        await s.CreateContractorAsync("b@example.com");
        await s.CreateContractorAsync("c@example.com");

        var byEmail = await s.Client.GetFromJsonAsync<List<ContractorResponse>>(s.Path("/contractors?email=B@example.com"));
        Assert.Equal("b@example.com", Assert.Single(byEmail!).Email);
        var page = await s.Client.GetFromJsonAsync<List<ContractorResponse>>(s.Path("/contractors?page=2&pageSize=2"));
        Assert.Single(page!);
    }

    [Fact]
    public async Task Update_Existing_PersistsAndReturns200()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var r = await s.Client.PutAsJsonAsync(s.Path($"/contractors/{c.Id}"), new UpdateContractorRequest("Jane Q", "jane@example.com", "acct_2"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var again = await s.Client.GetFromJsonAsync<ContractorResponse>(s.Path($"/contractors/{c.Id}"));
        Assert.Equal("Jane Q", again!.Name);
    }

    [Fact]
    public async Task Delete_Existing_Returns204_ThenHiddenFromListButStillFetchableAsInactive()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync(s.Path($"/contractors/{c.Id}"))).StatusCode);
        Assert.Empty((await s.Client.GetFromJsonAsync<List<ContractorResponse>>(s.Path("/contractors")))!);
        Assert.False((await s.Client.GetFromJsonAsync<ContractorResponse>(s.Path($"/contractors/{c.Id}")))!.IsActive);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.DeleteAsync(s.Path($"/contractors/{Guid.NewGuid()}"))).StatusCode);
    }
}

public class InvoiceEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task Create_ValidRequest_Returns201_DefaultsCurrencyFromPreferences()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var r = await s.Client.PostAsJsonAsync(s.Path("/invoices"),
            new CreateInvoiceRequest(c.Id, "INV-9", 250m, null, new DateOnly(2027, 1, 1), null));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var inv = (await r.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        Assert.Equal("USD", inv.Currency);
        Assert.Equal("Submitted", inv.Status);
        Assert.EndsWith($"/invoices/{inv.Id}", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Create_UnknownContractor_Returns404()
    {
        var s = await factory.CreateSessionAsync();
        var r = await s.Client.PostAsJsonAsync(s.Path("/invoices"),
            new CreateInvoiceRequest(Guid.NewGuid(), "INV-1", 5m, "USD", new DateOnly(2027, 1, 1), null));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Create_NonPositiveAmount_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var r = await s.Client.PostAsJsonAsync(s.Path("/invoices"),
            new CreateInvoiceRequest(c.Id, "INV-1", 0m, "USD", new DateOnly(2027, 1, 1), null));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateInvoiceNumber_Returns409()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        await s.CreateInvoiceAsync(c.Id, "INV-1");
        var r = await s.Client.PostAsJsonAsync(s.Path("/invoices"),
            new CreateInvoiceRequest(c.Id, "INV-1", 5m, "USD", new DateOnly(2027, 1, 1), null));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Get_Existing_Returns200_Unknown_Returns404()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(s.Path($"/invoices/{inv.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(s.Path($"/invoices/{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task List_FiltersByStatus_AndRejectsUnknownStatus()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        await s.CreateInvoiceAsync(c.Id, "INV-1");
        var inv2 = await s.CreateInvoiceAsync(c.Id, "INV-2");
        await s.Client.DeleteAsync(s.Path($"/invoices/{inv2.Id}"));

        Assert.Single((await s.Client.GetFromJsonAsync<List<InvoiceResponse>>(s.Path("/invoices?status=Void")))!);
        Assert.Equal(2, (await s.Client.GetFromJsonAsync<List<InvoiceResponse>>(s.Path("/invoices")))!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync(s.Path("/invoices?status=Bogus"))).StatusCode);
    }

    [Fact]
    public async Task Update_Existing_PersistsAndReturns200()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var r = await s.Client.PutAsJsonAsync(s.Path($"/invoices/{inv.Id}"), new UpdateInvoiceRequest(999m, null, new DateOnly(2027, 2, 2), "edited"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var again = await s.Client.GetFromJsonAsync<InvoiceResponse>(s.Path($"/invoices/{inv.Id}"));
        Assert.Equal(999m, again!.Amount);
        Assert.Equal("edited", again.Description);
    }

    [Fact]
    public async Task Delete_Existing_Voids_AndSecondDeleteIsConflict()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync(s.Path($"/invoices/{inv.Id}"))).StatusCode);
        Assert.Equal("Void", (await s.Client.GetFromJsonAsync<InvoiceResponse>(s.Path($"/invoices/{inv.Id}")))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync(s.Path($"/invoices/{inv.Id}"))).StatusCode);
    }

    [Fact]
    public async Task UpdateAndDelete_AlreadyPaid_Return409()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id, amount: 50m);
        var payment = (await (await s.PostPaymentAsync(inv.Id)).Content.ReadFromJsonAsync<PaymentResponse>())!;
        await s.WaitForPaymentStatusAsync(payment.Id, "Completed");

        var put = await s.Client.PutAsJsonAsync(s.Path($"/invoices/{inv.Id}"), new UpdateInvoiceRequest(5m, null, new DateOnly(2027, 1, 1), null));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync(s.Path($"/invoices/{inv.Id}"))).StatusCode);
    }
}

public class CurrentCompanyEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task Me_ReturnsTheCompanyOwningTheKey()
    {
        var s = await factory.CreateSessionAsync("Who Am I");
        var me = await s.Client.GetFromJsonAsync<CompanyResponse>(ApiRoutes.Me);
        Assert.Equal(s.CompanyId, me!.Id);
    }

    [Fact]
    public async Task Me_WithoutAKey_Returns401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(ApiRoutes.Me)).StatusCode);
    }
}
