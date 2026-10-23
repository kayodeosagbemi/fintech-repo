using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;
using Plooto.Ap.Services.Payment;
using Plooto.Ap.Services.Tests.TestSupport;

namespace Plooto.Ap.Services.Tests;

public class OutboxDispatcherTests
{
    [Fact]
    public async Task DispatchPending_RelaysRows_MarksDispatched_AndRingsTheQueue()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync();
        await host.CreatePaymentAsync(co.Id, inv.Id, "k1");

        var relayed = await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(default);

        Assert.Equal(1, relayed);
        var row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync());
        Assert.Equal(OutboxStatus.Dispatched, row.Status);
        var reader = host.Services.GetRequiredService<ChannelRegistry>().Reader(QueueNames.ValidatedPayments);
        Assert.True(reader.TryRead(out var signal));
        Assert.Equal(row.Id, signal.EventId);
    }

    [Fact]
    public async Task DispatchPending_TransportFailure_RecordsFailureWithBackoff_ThenDeadLetters()
    {
        await using var host = new PipelineHost(overrides: s => s.AddSingleton<IMessageTransport>(new FailingTransport()));
        var (co, _, inv) = await host.SeedAsync();
        await host.CreatePaymentAsync(co.Id, inv.Id, "k1");
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher>();

        await dispatcher.DispatchPendingAsync(default);
        var row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync());
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);   // backed off, not retried immediately
        Assert.Contains("broker down", row.LastError);
        Assert.Equal(0, await dispatcher.DispatchPendingAsync(default));

        // Force the remaining attempts by making the row due again each time.
        for (var i = 1; i < PaymentEvent.MaxAttempts; i++)
        {
            await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET NextAttemptAt = 0"));
            await dispatcher.DispatchPendingAsync(default);
        }
        row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync());
        Assert.Equal(OutboxStatus.DeadLettered, row.Status);
        await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET NextAttemptAt = 0"));
        Assert.Equal(0, await dispatcher.DispatchPendingAsync(default));   // dead-lettered rows are never retried
    }

    [Fact]
    public async Task SweepStuck_RequeuesDispatchedRowsOlderThanThreshold()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync();
        await host.CreatePaymentAsync(co.Id, inv.Id, "k1");
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchPendingAsync(default);

        Assert.Equal(0, await dispatcher.SweepStuckAsync(TimeSpan.FromMinutes(5), default));   // too fresh to be "stuck"
        Assert.Equal(1, await dispatcher.SweepStuckAsync(TimeSpan.Zero, default));

        Assert.Equal(OutboxStatus.Pending, (await host.QueryAsync(db => db.PaymentEvents.SingleAsync())).Status);
        Assert.Equal(1, await dispatcher.DispatchPendingAsync(default));   // and it is delivered again
    }

    private sealed class FailingTransport : IMessageTransport
    {
        public ValueTask SendAsync(string queueName, OutboxSignal signal, CancellationToken ct) =>
            throw new InvalidOperationException("broker down");
    }
}

public class QueueConsumerTests
{
    private sealed class ThrowingConsumer(ChannelRegistry c, IServiceScopeFactory s)
        : QueueConsumer("test-queue", c, s, NullLogger.Instance)
    {
        public int Calls;
        public bool ShouldThrow = true;
        protected override Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
        {
            Calls++;
            // Half-apply some state before failing to prove it is discarded.
            services.GetRequiredService<ApDbContext>().PaymentEvents.Add(PaymentEvent.Create(evt.PaymentId, evt.CompanyId, "Junk", "x", "{}"));
            if (ShouldThrow) throw new InvalidOperationException("handler exploded");
            return Task.CompletedTask;
        }
    }

    private static async Task<long> InsertDispatchedRowAsync(PipelineHost host)
    {
        return await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ApDbContext>();
            var e = PaymentEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "T", "test-queue", "{}");
            db.PaymentEvents.Add(e);
            await db.SaveChangesAsync();
            e.MarkDispatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return e.Id;
        });
    }

    [Fact]
    public async Task HandlerFailure_DiscardsPartialWork_RecordsAttempt_AndRetriesUntilDeadLettered()
    {
        await using var host = new PipelineHost();
        var consumer = new ThrowingConsumer(host.Services.GetRequiredService<ChannelRegistry>(), host.Services.GetRequiredService<IServiceScopeFactory>());
        var id = await InsertDispatchedRowAsync(host);

        for (var attempt = 1; attempt <= PaymentEvent.MaxAttempts; attempt++)
        {
            await consumer.ProcessAsync(new OutboxSignal(id), default);
            var row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync(e => e.Id == id));
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(attempt == PaymentEvent.MaxAttempts ? OutboxStatus.DeadLettered : OutboxStatus.Pending, row.Status);
            // simulate the dispatcher re-delivering it
            if (row.Status == OutboxStatus.Pending)
                await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET Status = 'Dispatched' WHERE Id = {0}", id));
        }

        Assert.Equal(PaymentEvent.MaxAttempts, consumer.Calls);
        Assert.Equal(1, await host.QueryAsync(db => db.PaymentEvents.CountAsync()));   // "Junk" rows never persisted
    }

    private sealed class PoisonFirstConsumer(ChannelRegistry c, IServiceScopeFactory s, long poisonId)
        : QueueConsumer("test-queue", c, s, NullLogger.Instance)
    {
        protected override async Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
        {
            if (evt.Id != poisonId) return;
            // Failure path where the row vanishes under us: the old code threw out of ProcessAsync and killed the loop.
            await services.GetRequiredService<ApDbContext>().Database.ExecuteSqlRawAsync("DELETE FROM PaymentEvents WHERE Id = {0}", evt.Id);
            throw new InvalidOperationException("poison");
        }
    }

    [Fact]
    public async Task ConsumerLoop_SurvivesAFailingSignal_AndProcessesTheNext()
    {
        await using var host = new PipelineHost();
        var registry = host.Services.GetRequiredService<ChannelRegistry>();
        var first = await InsertDispatchedRowAsync(host);
        var second = await InsertDispatchedRowAsync(host);
        var consumer = new PoisonFirstConsumer(registry, host.Services.GetRequiredService<IServiceScopeFactory>(), first);

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await registry.SendAsync("test-queue", new OutboxSignal(first), default);
            await registry.SendAsync("test-queue", new OutboxSignal(second), default);

            Assert.True(await host.WaitUntilAsync(async db =>
                (await db.PaymentEvents.SingleAsync(e => e.Id == second)).Status == OutboxStatus.Processed));
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DuplicateOrUnknownSignal_IsIgnored()
    {
        await using var host = new PipelineHost();
        var consumer = new ThrowingConsumer(host.Services.GetRequiredService<ChannelRegistry>(), host.Services.GetRequiredService<IServiceScopeFactory>())
        { ShouldThrow = false };
        var id = await InsertDispatchedRowAsync(host);

        await consumer.ProcessAsync(new OutboxSignal(id), default);
        await consumer.ProcessAsync(new OutboxSignal(id), default);   // duplicate doorbell
        await consumer.ProcessAsync(new OutboxSignal(99999), default);

        Assert.Equal(1, consumer.Calls);
        Assert.Equal(OutboxStatus.Processed, (await host.QueryAsync(db => db.PaymentEvents.SingleAsync(e => e.Id == id))).Status);
    }
}

public class PaymentProcessorTests
{
    private static async Task<(PipelineHost Host, Domain.Entities.Payment Payment, PaymentProcessor Processor, OutboxSignal Signal)>
        ArrangeAsync(decimal amount, TimeSpan? railTimeout = null)
    {
        var host = new PipelineHost(p => { if (railTimeout is { } t) p.RailTimeout = t; });
        var (co, _, inv) = await host.SeedAsync(amount: amount);
        var created = await host.CreatePaymentAsync(co.Id, inv.Id, "k1");
        await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(default);
        var evtId = (await host.QueryAsync(db => db.PaymentEvents.SingleAsync())).Id;
        return (host, created.Value!, host.Services.GetRequiredService<PaymentProcessor>(), new OutboxSignal(evtId));
    }

    [Fact]
    public async Task Success_CompletesPayment_PaysInvoice_AndEmitsCdcEvent()
    {
        var (host, payment, processor, signal) = await ArrangeAsync(250m);
        await using var _ = host;

        await processor.ProcessAsync(signal, default);

        var p = await host.QueryAsync(db => db.Payments.SingleAsync());
        Assert.Equal(PaymentStatus.Completed, p.Status);
        Assert.Equal(InvoiceStatus.Paid, (await host.QueryAsync(db => db.Invoices.SingleAsync())).Status);
        var events = await host.QueryAsync(db => db.PaymentEvents.ToListAsync());
        Assert.Contains(events, e => e.QueueName == QueueNames.PaymentEventsCdc && e.EventType == nameof(PaymentProcessed));
        Assert.Equal(OutboxStatus.Processed, events.Single(e => e.Id == signal.EventId).Status);
    }

    [Fact]
    public async Task RailDecline_FailsPayment_LeavesInvoiceUnpaid_AndEmitsFailureEvent()
    {
        var (host, _, processor, signal) = await ArrangeAsync(250.13m);
        await using var _ = host;

        await processor.ProcessAsync(signal, default);

        var p = await host.QueryAsync(db => db.Payments.SingleAsync());
        Assert.Equal(PaymentStatus.Failed, p.Status);
        Assert.Contains("Declined", p.FailureReason);
        Assert.Equal(InvoiceStatus.Approved, (await host.QueryAsync(db => db.Invoices.SingleAsync())).Status);
        Assert.Contains(await host.QueryAsync(db => db.PaymentEvents.ToListAsync()), e => e.EventType == nameof(PaymentFailed));
    }

    [Fact]
    public async Task HungRail_RetriesWithBackoff_AndFailsOnlyOnTheFinalAttempt()
    {
        var (host, _, processor, signal) = await ArrangeAsync(250.66m, railTimeout: TimeSpan.FromMilliseconds(100));
        await using var _ = host;

        for (var attempt = 1; attempt <= PaymentEvent.MaxAttempts; attempt++)
        {
            await processor.ProcessAsync(signal, default);
            var p = await host.QueryAsync(db => db.Payments.SingleAsync());
            var row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync(e => e.Id == signal.EventId));
            if (attempt < PaymentEvent.MaxAttempts)
            {
                Assert.Equal(attempt, row.AttemptCount);
                Assert.Equal(PaymentStatus.Processing, p.Status);
                Assert.Equal(OutboxStatus.Pending, row.Status);
                await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET Status = 'Dispatched' WHERE Id = {0}", signal.EventId));
            }
            else
            {
                Assert.Equal(OutboxStatus.Processed, row.Status);   // final attempt fails the payment and acks the event
                Assert.Equal(PaymentStatus.Failed, p.Status);
                Assert.Contains("timed out", p.FailureReason);
            }
        }
    }

    private sealed class StubPaymentService(IPaymentService inner, bool completeFirst) : IPaymentService
    {
        public Task<ServiceResult<Domain.Entities.Payment>> CreatePaymentAsync(ValidatedPaymentRequest r, string k, PaymentMethod m, Guid? b, CancellationToken ct) =>
            inner.CreatePaymentAsync(r, k, m, b, ct);
        public Task<Domain.Entities.Payment?> FindByIdempotencyKeyAsync(Guid c, string k, CancellationToken ct) => inner.FindByIdempotencyKeyAsync(c, k, ct);
        public Task<ServiceResult<Domain.Entities.Payment>> ResolveApprovalAsync(Guid c, Guid a, ApprovalDecision d, string? e, CancellationToken ct) =>
            inner.ResolveApprovalAsync(c, a, d, e, ct);
        public Task<Domain.Entities.Payment?> BeginProcessingAsync(Guid id, CancellationToken ct) => inner.BeginProcessingAsync(id, ct);
        public async Task<Domain.Entities.Payment?> CompletePaymentAsync(Guid id, CancellationToken ct)
        {
            if (completeFirst) await inner.CompletePaymentAsync(id, ct);
            return null;   // simulates "not applicable / lost the race"
        }
        public Task<Domain.Entities.Payment?> FailPaymentAsync(Guid id, string reason, CancellationToken ct) => inner.FailPaymentAsync(id, reason, ct);
    }

    private static async Task<(PipelineHost Host, PaymentProcessor Processor, OutboxSignal Signal)> ArrangeWithNullCompleteAsync(bool completeFirst)
    {
        var host = new PipelineHost(overrides: s => s.AddScoped<IPaymentService>(sp =>
            new StubPaymentService(ActivatorUtilities.CreateInstance<PaymentService>(sp), completeFirst)));
        var (co, _, inv) = await host.SeedAsync(amount: 100m);
        await host.CreatePaymentAsync(co.Id, inv.Id, "k1");
        await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(default);
        var evtId = (await host.QueryAsync(db => db.PaymentEvents.SingleAsync())).Id;
        return (host, host.Services.GetRequiredService<PaymentProcessor>(), new OutboxSignal(evtId));
    }

    [Fact]
    public async Task NullCompleteResult_WhilePaymentStillProcessing_IsRetried_NotSilentlyAcknowledged()
    {
        var (host, processor, signal) = await ArrangeWithNullCompleteAsync(completeFirst: false);
        await using var _ = host;

        await processor.ProcessAsync(signal, default);

        var row = await host.QueryAsync(db => db.PaymentEvents.SingleAsync(e => e.Id == signal.EventId));
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(PaymentStatus.Processing, (await host.QueryAsync(db => db.Payments.SingleAsync())).Status);
    }

    [Fact]
    public async Task NullCompleteResult_WhenPaymentAlreadyTerminal_IsTreatedAsDuplicate()
    {
        var (host, processor, signal) = await ArrangeWithNullCompleteAsync(completeFirst: true);
        await using var _ = host;

        await processor.ProcessAsync(signal, default);

        Assert.Equal(OutboxStatus.Processed, (await host.QueryAsync(db => db.PaymentEvents.SingleAsync(e => e.Id == signal.EventId))).Status);
        Assert.Equal(PaymentStatus.Completed, (await host.QueryAsync(db => db.Payments.SingleAsync())).Status);
    }

    [Fact]
    public async Task RedeliveryAfterCrash_PaymentAlreadyProcessing_IsCompleted()
    {
        var (host, payment, processor, signal) = await ArrangeAsync(100m);
        await using var _ = host;
        // Simulate: a previous worker moved it to Processing then crashed before the rail result.
        await host.WithScopeAsync(sp => sp.GetRequiredService<IPaymentService>().BeginProcessingAsync(payment.Id, default));

        await processor.ProcessAsync(signal, default);

        Assert.Equal(PaymentStatus.Completed, (await host.QueryAsync(db => db.Payments.SingleAsync())).Status);
    }

    [Fact]
    public async Task DuplicateDelivery_AfterCompletion_DoesNotPayTwice()
    {
        var (host, _, processor, signal) = await ArrangeAsync(100m);
        await using var _ = host;
        await processor.ProcessAsync(signal, default);
        // Re-open the row as if the doorbell rang again.
        await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET Status = 'Dispatched' WHERE Id = {0}", signal.EventId));

        await processor.ProcessAsync(signal, default);

        Assert.Equal(1, await host.QueryAsync(db => db.PaymentEvents.CountAsync(e => e.EventType == nameof(PaymentProcessed))));
    }
}

public class EndToEndPipelineTests
{
    [Fact]
    public async Task AutoApprovedPayment_FlowsThroughEveryQueue_ToCompletedAndStatement()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync(amount: 480m);
        await host.StartAsync();

        var created = await host.CreatePaymentAsync(co.Id, inv.Id, "e2e-1");
        Assert.Equal(ServiceOutcome.Ok, created.Outcome);

        Assert.True(await host.WaitUntilAsync(async db => (await db.Payments.SingleAsync()).Status == PaymentStatus.Completed),
            "payment should reach Completed");
        Assert.True(await host.WaitUntilAsync(async db =>
                await db.StatementLines.CountAsync() == 1 &&
                !await db.PaymentEvents.AnyAsync(e => e.Status != OutboxStatus.Processed && e.Status != OutboxStatus.DeadLettered)),
            "statement projected and every outbox row processed");

        var line = await host.QueryAsync(db => db.StatementLines.SingleAsync());
        Assert.Equal(StatementLineTypes.Payment, line.Type);
        Assert.Equal(480m, line.Amount);
        var events = await host.QueryAsync(db => db.PaymentEvents.ToListAsync());
        Assert.DoesNotContain(events, e => e.Status == OutboxStatus.DeadLettered);
        // validated-payments, payment-events-cdc, 2x pymt-queue fan-out, final notification
        Assert.Contains(events, e => e.QueueName == QueueNames.PymtQueue);
        Assert.Contains(events, e => e.QueueName == QueueNames.PymtQueueRails);
        Assert.Contains(events, e => e.QueueName == QueueNames.Notifications && e.EventType == nameof(PaymentNotification));
    }

    [Fact]
    public async Task ApprovalRequired_WaitsForApprover_ThenCompletes()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync(amount: 9000m, requireApproval: true, threshold: 1000m);
        await host.StartAsync();

        var created = await host.CreatePaymentAsync(co.Id, inv.Id, "e2e-approve");
        Assert.True(await host.WaitUntilAsync(async db =>
            await db.PaymentEvents.AnyAsync(e => e.EventType == nameof(ApprovalRequested) && e.Status == OutboxStatus.Processed)));
        Assert.Equal(PaymentStatus.Pending, (await host.QueryAsync(db => db.Payments.SingleAsync())).Status);   // nothing processed yet

        var approvalId = (await host.QueryAsync(db => db.ApprovalRequests.SingleAsync())).Id;
        await host.WithScopeAsync(sp => sp.GetRequiredService<IPaymentService>()
            .ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Approve, "boss@example.com", default));

        Assert.True(await host.WaitUntilAsync(async db => (await db.Payments.SingleAsync()).Status == PaymentStatus.Completed));
    }

    [Fact]
    public async Task RejectedApproval_NeverReachesTheRail()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync(amount: 9000m, requireApproval: true, threshold: 1000m);
        await host.StartAsync();
        await host.CreatePaymentAsync(co.Id, inv.Id, "e2e-reject");
        var approvalId = (await host.QueryAsync(db => db.ApprovalRequests.SingleAsync())).Id;

        // The ApprovalRequested notification must have drained first, so the final wait cannot pass vacuously.
        Assert.True(await host.WaitUntilAsync(async db =>
            await db.PaymentEvents.AnyAsync(e => e.EventType == nameof(ApprovalRequested) && e.Status == OutboxStatus.Processed)));

        await host.WithScopeAsync(sp => sp.GetRequiredService<IPaymentService>()
            .ResolveApprovalAsync(co.Id, approvalId, ApprovalDecision.Reject, "boss@example.com", default));

        Assert.True(await host.WaitUntilAsync(async db =>
            await db.PaymentEvents.AnyAsync(e => e.EventType == nameof(PaymentNotification)) &&
            !await db.PaymentEvents.AnyAsync(e => e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Dispatched)));
        Assert.Equal(PaymentStatus.Cancelled, (await host.QueryAsync(db => db.Payments.SingleAsync())).Status);
        Assert.Equal(0, await host.QueryAsync(db => db.StatementLines.CountAsync()));
    }

    [Fact]
    public async Task OutboxRowsLeftDispatchedBeforeARestart_AreRecoveredOnStartup()
    {
        await using var host = new PipelineHost();
        var (co, _, inv) = await host.SeedAsync();
        await host.CreatePaymentAsync(co.Id, inv.Id, "crash");
        // Simulate a crash after dispatch: row is Dispatched but the (in-memory) doorbell is gone.
        await host.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE PaymentEvents SET Status = 'Dispatched', DispatchedAt = 0"));

        await host.StartAsync();

        Assert.True(await host.WaitUntilAsync(async db => (await db.Payments.SingleAsync()).Status == PaymentStatus.Completed));
    }
}
