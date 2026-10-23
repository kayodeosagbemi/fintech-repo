using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Infrastructure.Security;

namespace Plooto.Ap.Tests.TestSupport;

/// <summary>Seeds the minimum object graph tests need.</summary>
public static class TestData
{
    public static async Task<Company> AddCompanyAsync(ApDbContext ctx, string slug = "acme",
        bool requireApproval = false, decimal threshold = 0m)
    {
        var company = Company.Create(slug, slug, ApiKey.Hash(Guid.NewGuid().ToString("N")));
        ctx.Companies.Add(company);
        ctx.CompanyPreferences.Add(CompanyPreferences.Create(company.Id, "USD", requireApproval, threshold, "ACH"));
        await ctx.SaveChangesAsync();
        return company;
    }

    public static async Task<Contractor> AddContractorAsync(ApDbContext ctx, Guid companyId, string email = "jane@example.com")
    {
        var contractor = Contractor.Create(companyId, "Jane", email, "acct_1");
        ctx.Contractors.Add(contractor);
        await ctx.SaveChangesAsync();
        return contractor;
    }

    public static async Task<Invoice> AddInvoiceAsync(ApDbContext ctx, Guid companyId, Guid contractorId,
        string number = "INV-1", decimal amount = 100m)
    {
        var invoice = Invoice.Create(companyId, contractorId, number, amount, "USD", new DateOnly(2027, 1, 1));
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }
}
