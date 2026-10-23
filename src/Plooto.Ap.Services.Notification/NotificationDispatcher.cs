using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Services.Notification;

/// <summary>
/// The "notixn srv": consumes the notifications queue and tells the world. Delivery here is a console log line (per the
/// README, "log to console for simplicity"); the approver acts through POST .../approvals/{id}/decision, the stand-in
/// for the out-of-scope Approver Service/UI.
/// </summary>
public sealed class NotificationDispatcher(ChannelRegistry channels, IServiceScopeFactory scopes, ILogger<NotificationDispatcher> logger)
    : QueueConsumer(QueueNames.Notifications, channels, scopes, logger)
{
    private readonly ILogger _log = logger;

    protected override Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
    {
        switch (evt.EventType)
        {
            case nameof(ApprovalRequested):
                var a = JsonSerializer.Deserialize<ApprovalRequested>(evt.PayloadJson)!;
                _log.LogInformation(
                    "[notify] Approval needed: {Amount} {Currency} payment {PaymentId} (company {CompanyId}). Resolve with POST /api/v1/companies/{CompanyId}/approvals/{ApprovalId}/decision",
                    a.Amount, a.Currency, a.PaymentId, a.CompanyId, a.CompanyId, a.ApprovalId);
                break;
            case nameof(PaymentNotification):
                var n = JsonSerializer.Deserialize<PaymentNotification>(evt.PayloadJson)!;
                _log.LogInformation("[notify] Payment {PaymentId} (company {CompanyId}): {Status} - {Message}",
                    n.PaymentId, n.CompanyId, n.Status, n.Message);
                break;
            default:
                _log.LogWarning("[notify] Ignoring unexpected event type {EventType} on notifications queue.", evt.EventType);
                break;
        }
        return Task.CompletedTask;
    }
}
