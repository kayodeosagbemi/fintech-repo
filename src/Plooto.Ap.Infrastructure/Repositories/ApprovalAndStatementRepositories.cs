using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Infrastructure.Repositories;

public interface IApprovalRepository
{
    Task<ApprovalRequest?> GetAsync(Guid companyId, Guid approvalId, CancellationToken ct);
    Task<ApprovalRequest?> GetByPaymentAsync(Guid companyId, Guid paymentId, CancellationToken ct);
    Task<IReadOnlyList<ApprovalRequest>> ListAsync(Guid companyId, ApprovalStatus? status, CancellationToken ct);
}

public class ApprovalRepository(ApDbContext db) : IApprovalRepository
{
    public Task<ApprovalRequest?> GetAsync(Guid companyId, Guid approvalId, CancellationToken ct) =>
        db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == approvalId, ct);

    public Task<ApprovalRequest?> GetByPaymentAsync(Guid companyId, Guid paymentId, CancellationToken ct) =>
        db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == companyId && a.PaymentId == paymentId, ct);

    public async Task<IReadOnlyList<ApprovalRequest>> ListAsync(Guid companyId, ApprovalStatus? status, CancellationToken ct)
    {
        var q = db.ApprovalRequests.AsNoTracking().Where(a => a.CompanyId == companyId);
        if (status is { } s) q = q.Where(a => a.Status == s);
        return await q.OrderBy(a => a.CreatedAt).ToListAsync(ct);
    }
}

public interface IStatementRepository
{
    /// <summary>Statement lines in chronological order. Balances are computed by the caller (decimal SUM isn't translatable on SQLite).</summary>
    Task<IReadOnlyList<StatementLine>> ListAsync(Guid companyId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
}

public class StatementRepository(ApDbContext db) : IStatementRepository
{
    public async Task<IReadOnlyList<StatementLine>> ListAsync(Guid companyId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var q = db.StatementLines.AsNoTracking().Where(l => l.CompanyId == companyId);
        if (from is { } f) q = q.Where(l => l.OccurredAt >= f);
        if (to is { } t) q = q.Where(l => l.OccurredAt <= t);
        return await q.OrderBy(l => l.OccurredAt).ThenBy(l => l.Id).ToListAsync(ct);
    }
}
