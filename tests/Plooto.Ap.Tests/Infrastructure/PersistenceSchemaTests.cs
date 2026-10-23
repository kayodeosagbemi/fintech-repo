using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Infrastructure.Security;
using Plooto.Ap.Tests.TestSupport;

namespace Plooto.Ap.Tests.Infrastructure;

public class PersistenceSchemaTests
{
    [Fact]
    public async Task Migrations_ApplyAndRoundTripCompanyWithDatesAndEnums()
    {
        using var db = new SqliteTestDb();
        var company = Company.Create("Acme", "acme", ApiKey.Hash("k"));
        await using (var ctx = db.CreateContext())
        {
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var loaded = await read.Companies.SingleAsync(c => c.Id == company.Id);
        Assert.Equal("acme", loaded.Slug);
        Assert.Equal(company.CreatedAt.UtcTicks, loaded.CreatedAt.UtcTicks);
    }

    [Fact]
    public async Task DateTimeOffset_SupportsOrderingAndRangeQueries()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var company = Company.Create("Acme", "acme", ApiKey.Hash("k"));
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        var count = await ctx.Companies.Where(c => c.CreatedAt > cutoff).OrderBy(c => c.CreatedAt).CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Invoice_CheckConstraint_RejectsNonPositiveAmountAtDbLevel()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var company = Company.Create("Acme", "acme", ApiKey.Hash("k"));
        var contractor = Contractor.Create(company.Id, "Jane", "jane@example.com", "acct");
        ctx.AddRange(company, contractor);
        await ctx.SaveChangesAsync();

        // Bypass the domain factory to prove the DB itself enforces the invariant.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO Invoices (Id, CompanyId, ContractorId, InvoiceNumber, Amount, Currency, Status, DueDate, CreatedAt) " +
            "VALUES ({0}, {1}, {2}, 'X', '-5', 'USD', 'Submitted', '2026-01-01', 0)",
            Guid.NewGuid(), company.Id, contractor.Id));
        Assert.Contains("CHECK", ex.ToString());
    }

    [Fact]
    public async Task DevSeeder_IsIdempotent()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        await DevSeeder.SeedAsync(ctx);
        await DevSeeder.SeedAsync(ctx);
        Assert.Equal(2, await ctx.Companies.CountAsync());
        Assert.Equal(6, await ctx.Contractors.CountAsync());
        Assert.Equal(6, await ctx.Invoices.CountAsync());
    }
}
