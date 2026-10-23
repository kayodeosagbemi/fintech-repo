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
/// <remarks>
/// Known gap: if a processing event is dead-lettered (all attempts exhausted by repeated non-rail errors, e.g. a
/// persistent DB fault) the payment is left in Processing and needs operator reconciliation. A reconciliation sweep is
/// out of scope for this take-home and is listed in DESIGN.md.
/// </remarks>
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
            var timeoutLimit = options.Value.RailTimeout;
            try
            {
                // Note: WaitAsync does not cancel the in-flight rail task, it only stops us waiting for it. That is acceptable
                // because the rail call is idempotent on PaymentId, so a retry cannot double-send.
                // WaitAsync enforces the timeout even if the rail ignores the token, so it can never block the reader.
                result = await rail.SendAsync(new PaymentRailRequest(payment.Id, payment.Rail ?? "ACH", payment.Amount,
                    payment.Currency, contractor.PayoutAccountRef), ct).WaitAsync(timeoutLimit, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Rail timeout or transient error: the transfer MAY have succeeded, so do not fail the payment yet.
                // Rethrow so the outbox backoff retries (the rail call is idempotent on PaymentId); fail only on the final attempt.
                var reason = ex is TimeoutException
                    ? $"Payment rail timed out after {evt.AttemptCount + 1} attempts"
                    : $"Payment rail error after {evt.AttemptCount + 1} attempts: {ex.Message}";
                if (evt.AttemptCount + 1 < PaymentEvent.MaxAttempts)
                {
                    logger.LogWarning(ex, "Payment rail unavailable for payment {PaymentId} (attempt {Attempt}); will retry.",
                        payment.Id, evt.AttemptCount + 1);
                    throw;
                }
                logger.LogError(ex, "Payment rail exhausted retries for payment {PaymentId}; failing it.", payment.Id);
                result = PaymentRailResult.Fail(reason);
            }
        }

        var outcome = result.Success
            ? await service.CompletePaymentAsync(payment.Id, ct)
            : await service.FailPaymentAsync(payment.Id, result.FailureReason ?? "Payment rail failed", ct);
        if (outcome is not null) return;

        // Null means "not applicable or lost a race". Only a terminal payment makes that a harmless duplicate.
        var current = await payments.GetAsync(evt.CompanyId, evt.PaymentId, ct);
        if (current is not null && current.Status is PaymentStatus.Completed or PaymentStatus.Failed or PaymentStatus.Cancelled)
            return;
        throw new InvalidOperationException(
            $"Could not record rail outcome for payment {evt.PaymentId} (status {current?.Status.ToString() ?? "missing"}); will retry.");
    }

    // A dead-lettered processing event is logged at Error by QueueConsumer; see the Known gap remark on this class.
}
