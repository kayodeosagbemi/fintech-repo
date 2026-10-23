using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Messaging;

/// <summary>
/// The "CDC" rectangle: consumes the captured payment events and republishes them onto "Pymt Queue". Because each
/// local channel has a single reader, "Pymt Queue" is fanned out by writing one outbox row per subscriber.
/// </summary>
public sealed class CdcProjector(ChannelRegistry channels, IServiceScopeFactory scopes, ILogger<CdcProjector> logger)
    : QueueConsumer(QueueNames.PaymentEventsCdc, channels, scopes, logger)
{
    protected override Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
    {
        var db = services.GetRequiredService<ApDbContext>();
        foreach (var queue in new[] { QueueNames.PymtQueue, QueueNames.PymtQueueRails })
            db.PaymentEvents.Add(PaymentEvent.Create(evt.PaymentId, evt.CompanyId, evt.EventType, queue, evt.PayloadJson));
        return Task.CompletedTask;   // committed together with MarkProcessed by the base class
    }
}
