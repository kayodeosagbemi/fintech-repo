using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Tests.TestSupport;

namespace Plooto.Ap.Tests.Infrastructure;

public class CompanyRepositoryTests
{
    [Fact]
    public async Task CreateAsync_PersistsCompanyAndPreferences()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var repo = new CompanyRepository(ctx);
        var company = Company.Create("Acme", "acme", "hash1");
        await repo.CreateAsync(company, CompanyPreferences.CreateDefault(company.Id), default);

        await using var read = db.CreateContext();
        var readRepo = new CompanyRepository(read);
        Assert.NotNull(await readRepo.GetAsync(company.Id, default));
        Assert.Equal("USD", (await readRepo.GetPreferencesAsync(company.Id, default))!.DefaultCurrency);
        Assert.Equal(company.Id, (await readRepo.GetByApiKeyHashAsync("hash1", default))!.Id);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        Assert.Null(await new CompanyRepository(ctx).GetAsync(Guid.NewGuid(), default));
    }
}

public class ContractorRepositoryTests
{
    [Fact]
    public async Task ListAsync_OnlyReturnsOwningCompanysContractors()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        await TestData.AddContractorAsync(ctx, a.Id, "x@a.com");
        await TestData.AddContractorAsync(ctx, b.Id, "y@b.com");

        var list = await new ContractorRepository(ctx).ListAsync(a.Id, 0, 50, null, default);
        Assert.Single(list);
        Assert.Equal(a.Id, list[0].CompanyId);
    }

    [Fact]
    public async Task GetAsync_OtherCompanysContractor_ReturnsNull()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        var c = await TestData.AddContractorAsync(ctx, a.Id);
        Assert.Null(await new ContractorRepository(ctx).GetAsync(b.Id, c.Id, default));
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse_DoesNotHardDelete()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var c = await TestData.AddContractorAsync(ctx, a.Id);
        var repo = new ContractorRepository(ctx);

        Assert.True(await repo.DeactivateAsync(a.Id, c.Id, default));

        await using var read = db.CreateContext();
        var row = await read.Contractors.SingleAsync();
        Assert.False(row.IsActive);
        Assert.Empty(await new ContractorRepository(read).ListAsync(a.Id, 0, 50, null, default));
    }

    [Fact]
    public async Task DeactivateAsync_UnknownContractor_ReturnsFalse()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        Assert.False(await new ContractorRepository(ctx).DeactivateAsync(a.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitiveAndCompanyScoped()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        await TestData.AddContractorAsync(ctx, a.Id, "jane@example.com");
        var repo = new ContractorRepository(ctx);
        Assert.NotNull(await repo.GetByEmailAsync(a.Id, " JANE@example.com ", default));
        Assert.Null(await repo.GetByEmailAsync(b.Id, "jane@example.com", default));
    }
}

public class InvoiceRepositoryTests
{
    [Fact]
    public async Task CreateAsync_DuplicateInvoiceNumberSameCompany_ThrowsDbUpdateException()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var c = await TestData.AddContractorAsync(ctx, a.Id);
        await TestData.AddInvoiceAsync(ctx, a.Id, c.Id, "INV-1");

        var dup = Invoice.Create(a.Id, c.Id, "INV-1", 5m, "USD", new DateOnly(2027, 1, 1));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => new InvoiceRepository(ctx).CreateAsync(dup, default));
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task CreateAsync_SameInvoiceNumberDifferentCompany_Succeeds()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        var ca = await TestData.AddContractorAsync(ctx, a.Id);
        var cb = await TestData.AddContractorAsync(ctx, b.Id);
        await TestData.AddInvoiceAsync(ctx, a.Id, ca.Id, "INV-1");
        await TestData.AddInvoiceAsync(ctx, b.Id, cb.Id, "INV-1");
        Assert.Equal(2, await ctx.Invoices.CountAsync());
    }

    [Fact]
    public async Task ListAsync_FiltersByStatusAndCompany()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        var ca = await TestData.AddContractorAsync(ctx, a.Id);
        var cb = await TestData.AddContractorAsync(ctx, b.Id);
        var approved = await TestData.AddInvoiceAsync(ctx, a.Id, ca.Id, "INV-1");
        await TestData.AddInvoiceAsync(ctx, a.Id, ca.Id, "INV-2");
        await TestData.AddInvoiceAsync(ctx, b.Id, cb.Id, "INV-3");
        approved.Approve();
        await ctx.SaveChangesAsync();

        var repo = new InvoiceRepository(ctx);
        var list = await repo.ListAsync(a.Id, InvoiceStatus.Approved, null, default);
        Assert.Single(list);
        Assert.Equal("INV-1", list[0].InvoiceNumber);
        Assert.Equal(2, (await repo.ListAsync(a.Id, null, null, default)).Count);
    }

    [Fact]
    public async Task GetAsync_OtherCompanysInvoice_ReturnsNull()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        var ca = await TestData.AddContractorAsync(ctx, a.Id);
        var inv = await TestData.AddInvoiceAsync(ctx, a.Id, ca.Id);
        Assert.Null(await new InvoiceRepository(ctx).GetAsync(b.Id, inv.Id, default));
    }
}

public class PaymentRepositoryTests
{
    private static async Task<(Company, Contractor, Invoice)> SeedAsync(ApDbContext ctx, string slug = "a", string number = "INV-1")
    {
        var company = await TestData.AddCompanyAsync(ctx, slug);
        var contractor = await TestData.AddContractorAsync(ctx, company.Id);
        var invoice = await TestData.AddInvoiceAsync(ctx, company.Id, contractor.Id, number);
        return (company, contractor, invoice);
    }

    private static Payment NewPayment(Company co, Contractor ct, Invoice inv, string key) =>
        Payment.Create(co.Id, inv.Id, ct.Id, inv.Amount, inv.Currency, PaymentMethod.Single, key);

    [Fact]
    public async Task CreateAsync_PersistsPayment()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co, ct, inv) = await SeedAsync(ctx);
        var p = await new PaymentRepository(ctx).CreateAsync(NewPayment(co, ct, inv, "k1"), default);

        await using var read = db.CreateContext();
        var loaded = await new PaymentRepository(read).GetAsync(co.Id, p.Id, default);
        Assert.Equal(PaymentStatus.Pending, loaded!.Status);
        Assert.Equal(100m, loaded.Amount);
    }

    [Fact]
    public async Task CreateAsync_DuplicateIdempotencyKeySameCompany_ThrowsDbUpdateException()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co, ct, inv) = await SeedAsync(ctx);
        var inv2 = await TestData.AddInvoiceAsync(ctx, co.Id, ct.Id, "INV-2");
        var repo = new PaymentRepository(ctx);
        await repo.CreateAsync(NewPayment(co, ct, inv, "k1"), default);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => repo.CreateAsync(NewPayment(co, ct, inv2, "k1"), default));
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task CreateAsync_SameIdempotencyKeyDifferentCompany_Succeeds()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co1, ct1, inv1) = await SeedAsync(ctx, "a");
        var (co2, ct2, inv2) = await SeedAsync(ctx, "b");
        var repo = new PaymentRepository(ctx);
        await repo.CreateAsync(NewPayment(co1, ct1, inv1, "k1"), default);
        await repo.CreateAsync(NewPayment(co2, ct2, inv2, "k1"), default);
        Assert.Equal(2, await ctx.Payments.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_SecondActivePaymentForSameInvoice_IsRejectedByDb()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co, ct, inv) = await SeedAsync(ctx);
        var repo = new PaymentRepository(ctx);
        await repo.CreateAsync(NewPayment(co, ct, inv, "k1"), default);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => repo.CreateAsync(NewPayment(co, ct, inv, "k2"), default));
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task CreateAsync_AfterFailedPayment_InvoiceCanBeRetried()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co, ct, inv) = await SeedAsync(ctx);
        var repo = new PaymentRepository(ctx);
        var first = await repo.CreateAsync(NewPayment(co, ct, inv, "k1"), default);
        first.Approve();
        first.MarkFailed("declined");
        await repo.UpdateAsync(first, default);

        await repo.CreateAsync(NewPayment(co, ct, inv, "k2"), default);
        Assert.Equal(2, await ctx.Payments.CountAsync());
        Assert.NotNull(await repo.GetActiveForInvoiceAsync(co.Id, inv.Id, default));
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_ReturnsExistingPayment_WhenPresent()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co, ct, inv) = await SeedAsync(ctx);
        var repo = new PaymentRepository(ctx);
        var p = await repo.CreateAsync(NewPayment(co, ct, inv, "k1"), default);
        Assert.Equal(p.Id, (await repo.GetByIdempotencyKeyAsync(co.Id, "k1", default))!.Id);
        Assert.Null(await repo.GetByIdempotencyKeyAsync(co.Id, "nope", default));
        Assert.Null(await repo.GetByIdempotencyKeyAsync(Guid.NewGuid(), "k1", default));
    }

    [Fact]
    public async Task ListAsync_OnlyReturnsOwningCompanysPayments_AndFilters()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co1, ct1, inv1) = await SeedAsync(ctx, "a");
        var (co2, ct2, inv2) = await SeedAsync(ctx, "b");
        var repo = new PaymentRepository(ctx);
        var p1 = await repo.CreateAsync(NewPayment(co1, ct1, inv1, "k1"), default);
        await repo.CreateAsync(NewPayment(co2, ct2, inv2, "k2"), default);

        var list = await repo.ListAsync(co1.Id, null, null, null, null, default);
        Assert.Equal(p1.Id, Assert.Single(list).Id);
        Assert.Empty(await repo.ListAsync(co1.Id, PaymentStatus.Completed, null, null, null, default));
        Assert.Single(await repo.ListAsync(co1.Id, null, null, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), default));
        Assert.Empty(await repo.ListAsync(co1.Id, null, null, DateTimeOffset.UtcNow.AddMinutes(5), null, default));
    }

    [Fact]
    public async Task GetAsync_OtherCompanysPayment_ReturnsNull()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var (co1, ct1, inv1) = await SeedAsync(ctx, "a");
        var (co2, _, _) = await SeedAsync(ctx, "b");
        var repo = new PaymentRepository(ctx);
        var p = await repo.CreateAsync(NewPayment(co1, ct1, inv1, "k1"), default);
        Assert.Null(await repo.GetAsync(co2.Id, p.Id, default));
    }
}

public class IdempotencyStoreTests
{
    [Fact]
    public async Task TryBegin_ThenComplete_ThenGet_ReturnsStoredResponse()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var co = await TestData.AddCompanyAsync(ctx);
        var store = new IdempotencyStore(ctx);

        Assert.True(await store.TryBeginAsync(co.Id, "k", "hash", default));
        Assert.Equal(IdempotencyStatus.InProgress, (await store.GetAsync(co.Id, "k", default))!.Status);
        await store.CompleteAsync(co.Id, "k", 201, "{\"ok\":true}", default);

        var rec = await store.GetAsync(co.Id, "k", default);
        Assert.Equal(IdempotencyStatus.Completed, rec!.Status);
        Assert.Equal(201, rec.ResponseStatusCode);
        Assert.Equal("{\"ok\":true}", rec.ResponseBody);
    }

    [Fact]
    public async Task TryBegin_DuplicateKeySameCompany_ReturnsFalse()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var co = await TestData.AddCompanyAsync(ctx);
        var store = new IdempotencyStore(ctx);
        Assert.True(await store.TryBeginAsync(co.Id, "k", "hash", default));
        Assert.False(await store.TryBeginAsync(co.Id, "k", "hash", default));
    }

    [Fact]
    public async Task TryBegin_SameKeyDifferentCompany_Succeeds()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var a = await TestData.AddCompanyAsync(ctx, "a");
        var b = await TestData.AddCompanyAsync(ctx, "b");
        var store = new IdempotencyStore(ctx);
        Assert.True(await store.TryBeginAsync(a.Id, "k", "h", default));
        Assert.True(await store.TryBeginAsync(b.Id, "k", "h", default));
    }

    [Fact]
    public async Task Abandon_RemovesInProgressOnly()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var co = await TestData.AddCompanyAsync(ctx);
        var store = new IdempotencyStore(ctx);
        await store.TryBeginAsync(co.Id, "k1", "h", default);
        await store.TryBeginAsync(co.Id, "k2", "h", default);
        await store.CompleteAsync(co.Id, "k2", 200, "{}", default);

        await store.AbandonAsync(co.Id, "k1", default);
        await store.AbandonAsync(co.Id, "k2", default);

        Assert.Null(await store.GetAsync(co.Id, "k1", default));
        Assert.NotNull(await store.GetAsync(co.Id, "k2", default));
    }
}
