using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Plooto.Ap.Messaging.Channels;

/// <summary>
/// A "doorbell": just the outbox row id. Consumers re-read the row (the source of truth) instead of trusting the
/// channel payload, so a lost or duplicated signal never loses or duplicates work (PLAN_1 section 5.4).
/// </summary>
public readonly record struct OutboxSignal(long EventId);

/// <summary>Moves a signal to the consumer of a named queue. The Service Bus swap-in point (section 3.3.2).</summary>
public interface IMessageTransport
{
    ValueTask SendAsync(string queueName, OutboxSignal signal, CancellationToken ct);
}

/// <summary>
/// Local transport: one in-process <see cref="Channel{T}"/> per named queue. A Channel is NOT visible outside this
/// process (PLAN_1 section 3.3.3). Each queue has exactly one consumer, so fan-out is done by the producer writing
/// one outbox row per subscriber (see <see cref="CdcProjector"/>), the local analogue of topic subscriptions.
/// </summary>
public sealed class ChannelRegistry : IMessageTransport
{
    private readonly ConcurrentDictionary<string, Channel<OutboxSignal>> _channels = new();

    private Channel<OutboxSignal> Get(string queueName) =>
        _channels.GetOrAdd(queueName, _ => Channel.CreateUnbounded<OutboxSignal>(
            new UnboundedChannelOptions { SingleReader = true }));

    public ChannelReader<OutboxSignal> Reader(string queueName) => Get(queueName).Reader;

    public ValueTask SendAsync(string queueName, OutboxSignal signal, CancellationToken ct) =>
        Get(queueName).Writer.WriteAsync(signal, ct);
}
