using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Infrastructure.Repositories;

// One thin repository per aggregate root. Every tenant-owned query takes an explicit companyId: that parameter is the
// single enforcement point for data isolation (PLAN_1 section 1.4 explains why global query filters were rejected).
// Create/Update methods save immediately; multi-entity transactions go through PaymentService, which uses the DbContext.

public interface ICompanyRepository
{
    Task<Company?> GetAsync(Guid id, CancellationToken ct);
    Task<Company?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken ct);
    Task<CompanyPreferences?> GetPreferencesAsync(Guid companyId, CancellationToken ct);
    Task<Company> CreateAsync(Company company, CompanyPreferences preferences, CancellationToken ct);
    Task<CompanyPreferences?> UpdatePreferencesAsync(Guid companyId, string defaultCurrency, bool requireApproval,
        decimal threshold, string rail, CancellationToken ct);
}

public class CompanyRepository(ApDbContext db) : ICompanyRepository
{
    public Task<Company?> GetAsync(Guid id, CancellationToken ct) =>
        db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Company?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken ct) =>
        db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.ApiKeyHash == apiKeyHash, ct);

    public Task<CompanyPreferences?> GetPreferencesAsync(Guid companyId, CancellationToken ct) =>
        db.CompanyPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);

    public async Task<CompanyPreferences?> UpdatePreferencesAsync(Guid companyId, string defaultCurrency, bool requireApproval,
        decimal threshold, string rail, CancellationToken ct)
    {
        var prefs = await db.CompanyPreferences.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        if (prefs is null) return null;
        prefs.Update(defaultCurrency, requireApproval, threshold, rail);
        await db.SaveChangesAsync(ct);
        return prefs;
    }

    public async Task<Company> CreateAsync(Company company, CompanyPreferences preferences, CancellationToken ct)
    {
        db.Companies.Add(company);
        db.CompanyPreferences.Add(preferences);
        await db.SaveChangesAsync(ct);
        return company;
    }
}

public interface IContractorRepository
{
    Task<Contractor?> GetAsync(Guid companyId, Guid contractorId, CancellationToken ct);
    Task<Contractor?> GetByEmailAsync(Guid companyId, string email, CancellationToken ct);
    Task<IReadOnlyList<Contractor>> ListAsync(Guid companyId, int skip, int take, string? email, CancellationToken ct);
    Task<Contractor> CreateAsync(Contractor contractor, CancellationToken ct);
    Task UpdateAsync(Contractor contractor, CancellationToken ct);
    /// <summary>Soft delete. Returns false when the contractor doesn't exist for this company.</summary>
    Task<bool> DeactivateAsync(Guid companyId, Guid contractorId, CancellationToken ct);
}

public class ContractorRepository(ApDbContext db) : IContractorRepository
{
    public Task<Contractor?> GetAsync(Guid companyId, Guid contractorId, CancellationToken ct) =>
        db.Contractors.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == contractorId, ct);

    public Task<Contractor?> GetByEmailAsync(Guid companyId, string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return db.Contractors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.IsActive && c.Email == normalized, ct);
    }

    public async Task<IReadOnlyList<Contractor>> ListAsync(Guid companyId, int skip, int take, string? email, CancellationToken ct)
    {
        var q = db.Contractors.AsNoTracking().Where(c => c.CompanyId == companyId && c.IsActive);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.Trim().ToLowerInvariant();
            q = q.Where(c => c.Email == normalized);
        }
        return await q.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<Contractor> CreateAsync(Contractor contractor, CancellationToken ct)
    {
        db.Contractors.Add(contractor);
        await db.SaveChangesAsync(ct);
        return contractor;
    }

    public Task UpdateAsync(Contractor contractor, CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<bool> DeactivateAsync(Guid companyId, Guid contractorId, CancellationToken ct)
    {
        var contractor = await GetAsync(companyId, contractorId, ct);
        if (contractor is null) return false;
        contractor.Deactivate();
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public interface IInvoiceRepository
{
    Task<Invoice?> GetAsync(Guid companyId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<Invoice>> ListAsync(Guid companyId, InvoiceStatus? status, Guid? contractorId, CancellationToken ct);
    Task<Invoice> CreateAsync(Invoice invoice, CancellationToken ct);
    Task UpdateAsync(Invoice invoice, CancellationToken ct);
}

public class InvoiceRepository(ApDbContext db) : IInvoiceRepository
{
    public Task<Invoice?> GetAsync(Guid companyId, Guid invoiceId, CancellationToken ct) =>
        db.Invoices.FirstOrDefaultAsync(i => i.CompanyId == companyId && i.Id == invoiceId, ct);

    public async Task<IReadOnlyList<Invoice>> ListAsync(Guid companyId, InvoiceStatus? status, Guid? contractorId, CancellationToken ct)
    {
        var q = db.Invoices.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (status is { } s) q = q.Where(i => i.Status == s);
        if (contractorId is { } c) q = q.Where(i => i.ContractorId == c);
        return await q.OrderBy(i => i.CreatedAt).ThenBy(i => i.InvoiceNumber).ToListAsync(ct);
    }

    public async Task<Invoice> CreateAsync(Invoice invoice, CancellationToken ct)
    {
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public Task UpdateAsync(Invoice invoice, CancellationToken ct) => db.SaveChangesAsync(ct);
}

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid companyId, Guid paymentId, CancellationToken ct);
    Task<Payment?> GetByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken ct);
    Task<Payment?> GetActiveForInvoiceAsync(Guid companyId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<Payment>> ListAsync(Guid companyId, PaymentStatus? status, Guid? contractorId,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
    Task<IReadOnlyList<Payment>> ListByBatchAsync(Guid companyId, Guid batchId, CancellationToken ct);
    Task<Payment> CreateAsync(Payment payment, CancellationToken ct);
    Task UpdateAsync(Payment payment, CancellationToken ct);
}

public class PaymentRepository(ApDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetAsync(Guid companyId, Guid paymentId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == paymentId, ct);

    public Task<Payment?> GetByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.IdempotencyKey == idempotencyKey, ct);

    public Task<Payment?> GetActiveForInvoiceAsync(Guid companyId, Guid invoiceId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.InvoiceId == invoiceId
            && Payment.ActiveStatuses.Contains(p.Status), ct);

    public async Task<IReadOnlyList<Payment>> ListAsync(Guid companyId, PaymentStatus? status, Guid? contractorId,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var q = db.Payments.AsNoTracking().Where(p => p.CompanyId == companyId);
        if (status is { } s) q = q.Where(p => p.Status == s);
        if (contractorId is { } c) q = q.Where(p => p.ContractorId == c);
        if (from is { } f) q = q.Where(p => p.CreatedAt >= f);
        if (to is { } t) q = q.Where(p => p.CreatedAt <= t);
        return await q.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Payment>> ListByBatchAsync(Guid companyId, Guid batchId, CancellationToken ct) =>
        await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.BatchId == batchId)
            .OrderBy(p => p.CreatedAt).ToListAsync(ct);

    public async Task<Payment> CreateAsync(Payment payment, CancellationToken ct)
    {
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    public Task UpdateAsync(Payment payment, CancellationToken ct) => db.SaveChangesAsync(ct);
}

public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> GetAsync(Guid companyId, string key, CancellationToken ct);
    /// <summary>Atomically claims the key. Returns false if a record for (company, key) already exists.</summary>
    Task<bool> TryBeginAsync(Guid companyId, string key, string requestHash, CancellationToken ct);
    Task CompleteAsync(Guid companyId, string key, int statusCode, string body, CancellationToken ct);
    /// <summary>Releases an InProgress claim after an unexpected failure so the client can retry the same key.</summary>
    Task AbandonAsync(Guid companyId, string key, CancellationToken ct);
}

public class IdempotencyStore(ApDbContext db) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> GetAsync(Guid companyId, string key, CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Key == key, ct);

    public async Task<bool> TryBeginAsync(Guid companyId, string key, string requestHash, CancellationToken ct)
    {
        var record = IdempotencyRecord.Begin(companyId, key, requestHash);   // validates inputs
        // A single atomic INSERT ... DO NOTHING is the claim: the PK decides the winner, and it bypasses the change
        // tracker so repeated claims within one DbContext can't hit an identity conflict.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO IdempotencyRecords (CompanyId, Key, RequestHash, Status, ResponseStatusCode, ResponseBody, CreatedAt)
            VALUES ({companyId}, {key}, {requestHash}, {nameof(IdempotencyStatus.InProgress)}, 0, '', {record.CreatedAt.UtcTicks})
            ON CONFLICT (CompanyId, Key) DO NOTHING", ct);
        return inserted == 1;
    }

    public async Task CompleteAsync(Guid companyId, string key, int statusCode, string body, CancellationToken ct)
    {
        var record = await db.IdempotencyRecords.FirstAsync(r => r.CompanyId == companyId && r.Key == key, ct);
        record.Complete(statusCode, body);
        await db.SaveChangesAsync(ct);
    }

    public async Task AbandonAsync(Guid companyId, string key, CancellationToken ct) =>
        await db.IdempotencyRecords
            .Where(r => r.CompanyId == companyId && r.Key == key && r.Status == IdempotencyStatus.InProgress)
            .ExecuteDeleteAsync(ct);
}
