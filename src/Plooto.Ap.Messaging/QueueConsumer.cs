using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Messaging;

/// <summary>
/// One consumer per named queue. The channel message only says "look at outbox row N": the consumer re-reads the row,
/// does the work in a fresh DI scope, and marks the row Processed only when the work finished. A crash before that
/// leaves the row Dispatched, and the dispatcher's sweep re-queues it. Handlers must therefore be idempotent.
/// </summary>
public abstract class QueueConsumer(
    string queueName,
    ChannelRegistry channels,
    IServiceScopeFactory scopes,
    ILogger logger) : BackgroundService
{
    public string QueueName { get; } = queueName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var signal in channels.Reader(QueueName).ReadAllAsync(stoppingToken))
                await ProcessAsync(signal, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Handles one signal end-to-end. Public so tests can drive the pipeline deterministically.</summary>
    public async Task ProcessAsync(OutboxSignal signal, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApDbContext>();

        var evt = await db.PaymentEvents.FirstOrDefaultAsync(e => e.Id == signal.EventId, ct);
        if (evt is null || evt.Status != OutboxStatus.Dispatched)
            return;   // already processed (duplicate signal) or not ours to touch

        try
        {
            await HandleAsync(scope.ServiceProvider, evt, ct);
            evt.MarkProcessed(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down: row stays Dispatched and is re-queued on the next start
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consumer {Queue} failed on event {EventId} ({EventType}).", QueueName, signal.EventId, evt.EventType);
            // Discard half-applied tracked state, then record the failure on a clean copy of the row.
            db.ChangeTracker.Clear();
            var row = await db.PaymentEvents.FirstAsync(e => e.Id == signal.EventId, ct);
            row.RecordFailure(ex.Message, DateTimeOffset.UtcNow);
            if (row.Status == OutboxStatus.DeadLettered)
                logger.LogError("Event {EventId} dead-lettered after {Attempts} attempts.", row.Id, row.AttemptCount);
            await db.SaveChangesAsync(ct);
        }
    }

    protected abstract Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct);
}
