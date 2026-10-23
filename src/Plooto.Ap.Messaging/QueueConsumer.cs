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
            {
                try
                {
                    await ProcessAsync(signal, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad signal must never kill the loop; the row stays Dispatched and the sweep re-queues it.
                    logger.LogError(ex, "Consumer {Queue} hit an unexpected error on event {EventId}; continuing.", QueueName, signal.EventId);
                }
            }
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
        catch (DbUpdateConcurrencyException ex) when (IsOutboxRowConflict(ex, evt))
        {
            // The sweep re-queued the row while we worked; redelivery is idempotent, so just step aside.
            logger.LogWarning("Event {EventId} on {Queue} was re-queued while being processed; ignoring this completion.", signal.EventId, QueueName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consumer {Queue} failed on event {EventId} ({EventType}).", QueueName, signal.EventId, evt.EventType);
            await RecordFailureAsync(db, signal.EventId, ex, ct);
        }
    }

    private static bool IsOutboxRowConflict(DbUpdateConcurrencyException ex, PaymentEvent evt) =>
        ex.Entries.Any(e => ReferenceEquals(e.Entity, evt));

    private async Task RecordFailureAsync(ApDbContext db, long eventId, Exception cause, CancellationToken ct)
    {
        // Discard half-applied tracked state, then record the failure on a clean copy of the row.
        db.ChangeTracker.Clear();
        var row = await db.PaymentEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (row is null || row.Status is not (OutboxStatus.Dispatched or OutboxStatus.Pending))
            return;   // gone, or already finished/dead-lettered by someone else
        try
        {
            row.RecordFailure(cause.Message, DateTimeOffset.UtcNow);
            if (row.Status == OutboxStatus.DeadLettered)
                logger.LogError("Event {EventId} dead-lettered after {Attempts} attempts.", row.Id, row.AttemptCount);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(ex, "Could not record failure for event {EventId}; leaving it for the sweep.", eventId);
        }
    }

    protected abstract Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct);
}
