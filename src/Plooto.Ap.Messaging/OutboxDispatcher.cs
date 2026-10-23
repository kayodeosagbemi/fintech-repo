using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Messaging;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int BatchSize { get; set; } = 50;
    /// <summary>A row Dispatched for longer than this is assumed lost (consumer crashed) and is re-queued.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Relays committed outbox rows onto the transport. This is the local approximation of CDC (PLAN_1 section 3.4): the
/// outbox row is the captured change; polling and republishing it is the propagation step. Delivery is at-least-once.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IMessageTransport transport,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Channels are empty after a restart, so anything still marked Dispatched can never be consumed.
        await RunSafelyAsync(() => SweepStuckAsync(TimeSpan.Zero, stoppingToken), stoppingToken);

        var nextSweep = DateTimeOffset.UtcNow + _options.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSafelyAsync(() => DispatchPendingAsync(stoppingToken), stoppingToken);
            if (DateTimeOffset.UtcNow >= nextSweep)
            {
                await RunSafelyAsync(() => SweepStuckAsync(_options.StuckAfter, stoppingToken), stoppingToken);
                nextSweep = DateTimeOffset.UtcNow + _options.SweepInterval;
            }
            try { await Task.Delay(_options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSafelyAsync(Func<Task<int>> work, CancellationToken ct)
    {
        try { await work(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Outbox dispatcher iteration failed; will retry."); }
    }

    /// <summary>Publishes every due Pending row. Returns the number relayed.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApDbContext>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.PaymentEvents
            .Where(e => e.Status == OutboxStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.Id).Take(_options.BatchSize).ToListAsync(ct);
        if (due.Count == 0) return 0;

        // Persist Dispatched BEFORE ringing the doorbell: a consumer that wakes instantly must see Dispatched.
        foreach (var e in due) e.MarkDispatched(now);
        await db.SaveChangesAsync(ct);

        foreach (var e in due)
        {
            try
            {
                await transport.SendAsync(e.QueueName, new OutboxSignal(e.Id), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                e.RecordFailure(ex.Message, now);
                LogIfDeadLettered(e);
            }
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>Re-queues rows stuck in Dispatched longer than <paramref name="olderThan"/>. Returns how many.</summary>
    public async Task<int> SweepStuckAsync(TimeSpan olderThan, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApDbContext>();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - olderThan;
        var stuck = await db.PaymentEvents
            .Where(e => e.Status == OutboxStatus.Dispatched && e.DispatchedAt <= cutoff).ToListAsync(ct);
        foreach (var e in stuck) e.Requeue(now);
        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Re-queued {Count} outbox event(s) stuck in Dispatched.", stuck.Count);
        }
        return stuck.Count;
    }

    private void LogIfDeadLettered(PaymentEvent e)
    {
        if (e.Status == OutboxStatus.DeadLettered)
            logger.LogError("Outbox event {EventId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}",
                e.Id, e.EventType, e.AttemptCount, e.LastError);
    }
}
