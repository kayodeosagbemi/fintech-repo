using System.Text.Json;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Messaging;

/// <summary>
/// What producers depend on. Swapping the transport (Channels today, Azure Service Bus later) never touches
/// producer code (PLAN_1 section 3.3).
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Stages the event for delivery. It becomes durable when the caller's unit of work commits, which is what
    /// makes the outbox atomic with the domain change.
    /// </summary>
    Task PublishAsync<T>(T message, string queueName, CancellationToken ct = default) where T : IntegrationEvent;
}

/// <summary>Transactional-outbox publisher: adds a <see cref="PaymentEvent"/> row to the caller's DbContext, without saving.</summary>
public sealed class OutboxEventPublisher(ApDbContext db) : IEventPublisher
{
    public Task PublishAsync<T>(T message, string queueName, CancellationToken ct = default) where T : IntegrationEvent
    {
        // Serialize as the concrete type so subclass fields are kept.
        var json = JsonSerializer.Serialize(message, message.GetType());
        db.PaymentEvents.Add(PaymentEvent.Create(message.PaymentId, message.CompanyId, message.GetType().Name, queueName, json));
        return Task.CompletedTask;
    }
}
