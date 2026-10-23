using Microsoft.Extensions.Options;

namespace Plooto.Ap.Services.Payment;

public sealed record PaymentRailRequest(Guid PaymentId, string Rail, decimal Amount, string Currency, string PayoutAccountRef);
public sealed record PaymentRailResult(bool Success, string? FailureReason = null)
{
    public static PaymentRailResult Ok() => new(true);
    public static PaymentRailResult Fail(string reason) => new(false, reason);
}

/// <summary>The "pmt rails" diamond: an external system (ACH, wire, card network).</summary>
public interface IPaymentRailClient
{
    /// <summary>
    /// <see cref="PaymentRailRequest.PaymentId"/> doubles as the rail-side idempotency key, so a redelivered message
    /// (at-least-once) can never cause a second transfer on a real rail.
    /// </summary>
    Task<PaymentRailResult> SendAsync(PaymentRailRequest request, CancellationToken ct);
}

public sealed class SimulatedRailOptions
{
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
/// Local stand-in for a real rail. Deterministic so behaviour is demonstrable by hand: an amount with cents of .13 is
/// declined, cents of .66 never answers (exercises the processor's timeout), everything else succeeds after a short delay.
/// </summary>
public sealed class SimulatedPaymentRailClient(IOptions<SimulatedRailOptions> options) : IPaymentRailClient
{
    public async Task<PaymentRailResult> SendAsync(PaymentRailRequest request, CancellationToken ct)
    {
        var cents = (int)Math.Round((request.Amount - Math.Truncate(request.Amount)) * 100m);
        if (cents == 66)
        {
            await Task.Delay(Timeout.Infinite, ct);   // hung rail
        }
        await Task.Delay(options.Value.Delay, ct);
        return cents == 13
            ? PaymentRailResult.Fail("Declined by simulated rail (insufficient funds)")
            : PaymentRailResult.Ok();
    }
}
