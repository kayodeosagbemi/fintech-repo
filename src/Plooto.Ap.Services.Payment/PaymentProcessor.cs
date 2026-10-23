using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Services.Payment;

public sealed class PaymentProcessorOptions
{
    /// <summary>A hung rail call must not strand a payment in Processing (PLAN_1 section 5.2).</summary>
    public TimeSpan RailTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Consumer half of the "Payment Service": takes cleared payments off validated-payments and sends them to the rail.</summary>
public sealed class PaymentProcessor(
    ChannelRegistry channels,
    IServiceScopeFactory scopes,
    IOptions<PaymentProcessorOptions> options,
    ILogger<PaymentProcessor> logger)
    : QueueConsumer(QueueNames.ValidatedPayments, channels, scopes, logger)
{
    protected override async Task HandleAsync(IServiceProvider services, PaymentEvent evt, CancellationToken ct)
    {
        var payments = services.GetRequiredService<IPaymentRepository>();
        var contractors = services.GetRequiredService<IContractorRepository>();
        var service = services.GetRequiredService<IPaymentService>();
        var rail = services.GetRequiredService<IPaymentRailClient>();

        // The row, not the message, is the truth: re-read the payment and act only on what state allows.
        var payment = await payments.GetAsync(evt.CompanyId, evt.PaymentId, ct);
        if (payment is null) return;

        if (payment.Status == PaymentStatus.Approved)
        {
            if (await service.BeginProcessingAsync(payment.Id, ct) is null) return;   // another worker took it
        }
        else if (payment.Status != PaymentStatus.Processing)
        {
            return;   // already completed/failed/cancelled: duplicate delivery
        }
        // Status == Processing here also covers redelivery after a crash; the rail call is idempotent on PaymentId.

        var contractor = await contractors.GetAsync(payment.CompanyId, payment.ContractorId, ct);
        PaymentRailResult result;
        if (contractor is null)
        {
            result = PaymentRailResult.Fail("Contractor no longer exists.");
        }
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.Value.RailTimeout);
                result = await rail.SendAsync(new PaymentRailRequest(payment.Id, payment.Rail ?? "ACH", payment.Amount,
                    payment.Currency, contractor.PayoutAccountRef), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = PaymentRailResult.Fail($"Payment rail timed out after {options.Value.RailTimeout.TotalSeconds:0.#}s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Payment rail threw for payment {PaymentId}.", payment.Id);
                result = PaymentRailResult.Fail($"Payment rail error: {ex.Message}");
            }
        }

        if (result.Success)
            await service.CompletePaymentAsync(payment.Id, ct);
        else
            await service.FailPaymentAsync(payment.Id, result.FailureReason ?? "Payment rail failed", ct);
    }
}
