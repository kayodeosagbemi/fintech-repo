using Plooto.Ap.Domain.Entities;

namespace Plooto.Ap.Domain.Common;

/// <summary>
/// Output of the Validation Service and input to the Payment Service. Lives in Domain so the two services
/// never need to reference each other (PLAN_1 section 4).
/// </summary>
public sealed record ValidatedPaymentRequest(Invoice Invoice, Contractor Contractor, CompanyPreferences Preferences);
