using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Services.Payment;

public static class StatementLineTypes
{
    public const string Payment = "Payment";   // debits the running balance
    public const string Failure = "Failure";   // informational, no balance effect
}

/// <summary>Maintains the statement read model from final payment events on the Pymt Queue. Idempotent per (payment, line type).</summary>
public sealed class StatementProjector(ChannelRegistry channels, IServiceScopeFactory scopes, ILogger<StatementProjector> logger)
    : QueueConsumer(QueueNames.PymtQueue, channels, scopes, logger)
{
    protected override async Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
    {
        var type = evt.EventType switch
        {
            nameof(PaymentProcessed) => StatementLineTypes.Payment,
            nameof(PaymentFailed) => StatementLineTypes.Failure,
            _ => null,
        };
        if (type is null) return;

        var db = services.GetRequiredService<ApDbContext>();
        // Duplicate delivery is handled by this existence check; the save happens in the base class, so a unique-index
        // race cannot be caught here and simply fails the attempt, which is retried (and then hits this check).
        if (await db.StatementLines.AnyAsync(l => l.PaymentId == evt.PaymentId && l.Type == type, ct)) return;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.CompanyId == evt.CompanyId && p.Id == evt.PaymentId, ct);
        if (payment is null) return;
        var contractor = await db.Contractors.FirstOrDefaultAsync(c => c.CompanyId == evt.CompanyId && c.Id == payment.ContractorId, ct);

        db.StatementLines.Add(StatementLine.Create(evt.CompanyId, payment.Id, type, contractor?.Name ?? "Unknown contractor",
            payment.Amount, payment.Currency, payment.CompletedAt ?? DateTimeOffset.UtcNow));
        // Saved with MarkProcessed by the base class; a unique-index race with a duplicate delivery fails the attempt and is retried.
    }
}
