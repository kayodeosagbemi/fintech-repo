using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Infrastructure.Persistence;

public class ApDbContext : DbContext
{
    public ApDbContext(DbContextOptions<ApDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyPreferences> CompanyPreferences => Set<CompanyPreferences>();
    public DbSet<Contractor> Contractors => Set<Contractor>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<StatementLine> StatementLines => Set<StatementLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't compare/order DateTimeOffset natively; store UTC ticks so range queries and ORDER BY work.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<UtcTicksConverter>();
        // Readable enum values in the database.
        configurationBuilder.Properties<InvoiceStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PaymentMethod>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ApprovalStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<IdempotencyStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<OutboxStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    private sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public UtcTicksConverter()
            : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)) { }
    }
}
