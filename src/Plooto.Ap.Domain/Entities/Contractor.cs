namespace Plooto.Ap.Domain.Entities;

public class Contractor
{
    private Contractor() { }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }                    // tenant isolation key
    public string Name { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string PayoutAccountRef { get; private set; } = default!; // opaque rail token, never a raw account number
    public bool IsActive { get; private set; } = true;

    public static Contractor Create(Guid companyId, string name, string email, string payoutAccountRef)
    {
        var c = new Contractor { Id = Guid.NewGuid(), CompanyId = companyId };
        c.Update(name, email, payoutAccountRef);
        return c;
    }

    public void Update(string name, string email, string payoutAccountRef)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) throw new ArgumentException("A valid email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(payoutAccountRef)) throw new ArgumentException("PayoutAccountRef is required.", nameof(payoutAccountRef));
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
        PayoutAccountRef = payoutAccountRef.Trim();
    }

    public void Deactivate() => IsActive = false;
}
