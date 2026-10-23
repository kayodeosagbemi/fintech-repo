using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Infrastructure.Persistence.Configurations;

// SQLite stores decimal as TEXT, so CHECKs cast to REAL to compare numerically.

internal class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(200).IsRequired();
        b.Property(x => x.ApiKeyHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => x.ApiKeyHash).IsUnique();   // auth lookup path
    }
}

internal class CompanyPreferencesConfiguration : IEntityTypeConfiguration<CompanyPreferences>
{
    public void Configure(EntityTypeBuilder<CompanyPreferences> b)
    {
        b.HasKey(x => x.CompanyId);
        b.HasOne<Company>().WithOne().HasForeignKey<CompanyPreferences>(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.PreferredPaymentRail).HasMaxLength(32).IsRequired();
    }
}

internal class ContractorConfiguration : IEntityTypeConfiguration<Contractor>
{
    public void Configure(EntityTypeBuilder<Contractor> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.PayoutAccountRef).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.Email });
    }
}

internal class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne<Contractor>().WithMany().HasForeignKey(x => x.ContractorId);
        b.Property(x => x.InvoiceNumber).HasMaxLength(100).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.InvoiceNumber }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Invoices_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0"));
    }
}

internal class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId);
        b.HasOne<Contractor>().WithMany().HasForeignKey(x => x.ContractorId);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Rail).HasMaxLength(32);
        b.Property(x => x.FailureReason).HasMaxLength(1000);
        b.HasIndex(x => x.CompanyId);
        b.HasIndex(x => x.InvoiceId);
        b.HasIndex(x => x.BatchId);
        // DB-level backstop for duplicate-payment prevention.
        b.HasIndex(x => new { x.CompanyId, x.IdempotencyKey }).IsUnique();
        // An invoice can have at most one live payment; Failed/Cancelled payments release it for a retry.
        b.HasIndex(x => new { x.CompanyId, x.InvoiceId }).IsUnique()
            .HasFilter("\"Status\" IN ('Pending','Approved','Processing','Completed')")
            .HasDatabaseName("UX_Payments_ActivePerInvoice");
        b.ToTable(t => t.HasCheckConstraint("CK_Payments_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0"));
    }
}

internal class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        b.Property(x => x.QueueName).HasMaxLength(100).IsRequired();
        b.Property(x => x.PayloadJson).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasIndex(x => x.PaymentId);
        // Dispatcher scan: only rows still waiting to be relayed.
        b.HasIndex(x => x.NextAttemptAt).HasFilter("\"Status\" = 'Pending'").HasDatabaseName("IX_PaymentEvents_Pending");
    }
}

internal class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.HasKey(x => new { x.CompanyId, x.Key });
        b.Property(x => x.Key).HasMaxLength(200).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        b.Property(x => x.ResponseBody).IsRequired();
    }
}

internal class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.ApproverEmail).HasMaxLength(320);
        b.HasIndex(x => x.CompanyId);
        b.HasIndex(x => x.PaymentId);
    }
}

internal class StatementLineConfiguration : IEntityTypeConfiguration<StatementLine>
{
    public void Configure(EntityTypeBuilder<StatementLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(32).IsRequired();
        b.Property(x => x.CounterParty).HasMaxLength(200).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.HasIndex(x => new { x.CompanyId, x.OccurredAt });
        // Projector is at-least-once, so the projection must be idempotent per (payment, line type).
        b.HasIndex(x => new { x.PaymentId, x.Type }).IsUnique();
    }
}
