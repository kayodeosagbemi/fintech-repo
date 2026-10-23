using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Messaging.ServiceBus;

/// <summary>
/// NOT implemented locally; marks the cloud swap-in point (PLAN_1 section 3.3.2). A real implementation would publish
/// to a single Azure Service Bus topic ("ap-events") with the queue name as a MessageType application property and
/// consumers on filtered subscriptions. Producers and consumers would not change.
/// </summary>
public sealed class ServiceBusMessageTransport : IMessageTransport
{
    public ValueTask SendAsync(string queueName, OutboxSignal signal, CancellationToken ct) =>
        throw new NotSupportedException("Azure Service Bus transport is a documented swap-in, not part of the local build.");
}
