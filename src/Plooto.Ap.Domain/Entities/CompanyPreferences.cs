namespace Plooto.Ap.Domain.Entities;

/// <summary>"tb company prefs" in the design diagram. 1:1 with <see cref="Company"/>.</summary>
public class CompanyPreferences
{
    private CompanyPreferences() { }

    public Guid CompanyId { get; private set; }
    public string DefaultCurrency { get; private set; } = "USD";
    public bool RequireApprovalAboveThreshold { get; private set; }
    public decimal ApprovalThreshold { get; private set; }
    public string PreferredPaymentRail { get; private set; } = "ACH";

    public static CompanyPreferences CreateDefault(Guid companyId) => new() { CompanyId = companyId };

    public static CompanyPreferences Create(Guid companyId, string defaultCurrency, bool requireApproval, decimal threshold, string rail)
    {
        if (threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold cannot be negative.");
        return new CompanyPreferences
        {
            CompanyId = companyId,
            DefaultCurrency = defaultCurrency.ToUpperInvariant(),
            RequireApprovalAboveThreshold = requireApproval,
            ApprovalThreshold = threshold,
            PreferredPaymentRail = rail,
        };
    }

    /// <summary>True when a payment of <paramref name="amount"/> must be signed off by a human before processing.</summary>
    public bool RequiresApproval(decimal amount) => RequireApprovalAboveThreshold && amount > ApprovalThreshold;
}
