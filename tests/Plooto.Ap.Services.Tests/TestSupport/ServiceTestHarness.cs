using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Infrastructure.Security;
using Plooto.Ap.Messaging;
using Plooto.Ap.Services.Payment;
using Plooto.Ap.Services.Validation;

namespace Plooto.Ap.Services.Tests.TestSupport;

/// <summary>Real migrated in-memory SQLite plus the service graph wired the same way the API wires it.</summary>
public sealed class ServiceTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public ServiceTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    public ApDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApDbContext>().UseSqlite(_connection).Options);

    /// <summary>A scope = one DbContext + the services bound to it (like one HTTP request).</summary>
    public Scope NewScope() => new(NewContext());

    public void Dispose() => _connection.Dispose();

    public sealed class Scope(ApDbContext db) : IAsyncDisposable
    {
        public ApDbContext Db { get; } = db;
        public ValidationService Validation => new(new InvoiceRepository(Db), new ContractorRepository(Db),
            new PaymentRepository(Db), new CompanyRepository(Db));
        public PaymentService Payments => new(Db, new OutboxEventPublisher(Db));
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    public static async Task<(Company Company, Contractor Contractor, Invoice Invoice)> SeedAsync(
        ApDbContext ctx, string slug = "acme", decimal amount = 100m, bool requireApproval = false, decimal threshold = 0m,
        string invoiceNumber = "INV-1")
    {
        var company = Company.Create(slug, slug, ApiKey.Hash(Guid.NewGuid().ToString("N")));
        ctx.Companies.Add(company);
        ctx.CompanyPreferences.Add(CompanyPreferences.Create(company.Id, "USD", requireApproval, threshold, "ACH"));
        var contractor = Contractor.Create(company.Id, "Jane", $"jane@{slug}.example", "acct_1");
        ctx.Contractors.Add(contractor);
        var invoice = Invoice.Create(company.Id, contractor.Id, invoiceNumber, amount, "USD", new DateOnly(2027, 1, 1));
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return (company, contractor, invoice);
    }

    public static async Task<Invoice> AddInvoiceAsync(ApDbContext ctx, Company company, Contractor contractor,
        string number, decimal amount = 100m)
    {
        var invoice = Invoice.Create(company.Id, contractor.Id, number, amount, "USD", new DateOnly(2027, 1, 1));
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }
}
