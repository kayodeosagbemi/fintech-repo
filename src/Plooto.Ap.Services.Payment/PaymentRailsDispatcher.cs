using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Services.Payment;

/// <summary>
/// Second subscriber on the Pymt Queue: turns the final rail outcome into a caller-facing notification, closing the
/// loop "Pymt Queue -> App Server/UI" without any push channel (the UI reads status by polling).
/// </summary>
public sealed class PaymentRailsDispatcher(ChannelRegistry channels, IServiceScopeFactory scopes, ILogger<PaymentRailsDispatcher> logger)
    : QueueConsumer(QueueNames.PymtQueueRails, channels, scopes, logger)
{
    protected override async Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
    {
        var publisher = services.GetRequiredService<IEventPublisher>();
        var notification = evt.EventType switch
        {
            nameof(PaymentProcessed) => new PaymentNotification(evt.PaymentId, evt.CompanyId, nameof(PaymentStatus.Completed), "Payment completed"),
            nameof(PaymentFailed) => new PaymentNotification(evt.PaymentId, evt.CompanyId, nameof(PaymentStatus.Failed), "Payment failed"),
            _ => null,
        };
        if (notification is not null)
            await publisher.PublishAsync(notification, QueueNames.Notifications, ct);
    }
}
