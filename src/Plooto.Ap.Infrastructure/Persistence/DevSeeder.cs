using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Security;

namespace Plooto.Ap.Infrastructure.Persistence;

/// <summary>Development-only sample data. Idempotent: does nothing if any company already exists.</summary>
public static class DevSeeder
{
    // Fixed, obviously-fake keys so a fresh checkout can call the API immediately. Dev only.
    public const string AcmeApiKey = "dev-acme-api-key";
    public const string GlobexApiKey = "dev-globex-api-key";

    public static async Task SeedAsync(ApDbContext db, CancellationToken ct = default)
    {
        if (await db.Companies.AnyAsync(ct)) return;

        await SeedCompanyAsync(db, "Acme Inc", "acme", AcmeApiKey, requireApproval: true, threshold: 5000m, ct);
        await SeedCompanyAsync(db, "Globex Corp", "globex", GlobexApiKey, requireApproval: false, threshold: 0m, ct);
        await db.SaveChangesAsync(ct);
    }

    private static Task SeedCompanyAsync(ApDbContext db, string name, string slug, string apiKey,
        bool requireApproval, decimal threshold, CancellationToken ct)
    {
        var company = Company.Create(name, slug, ApiKey.Hash(apiKey));
        db.Companies.Add(company);
        db.CompanyPreferences.Add(CompanyPreferences.Create(company.Id, "USD", requireApproval, threshold, "ACH"));

        var contractors = new[]
        {
            Contractor.Create(company.Id, "Jane Doe", $"jane.doe@{slug}-contractors.example", "acct_jane"),
            Contractor.Create(company.Id, "Sam Lee", $"sam.lee@{slug}-contractors.example", "acct_sam"),
            Contractor.Create(company.Id, "Priya Patel", $"priya.patel@{slug}-contractors.example", "acct_priya"),
        };
        db.Contractors.AddRange(contractors);

        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        db.Invoices.AddRange(
            Invoice.Create(company.Id, contractors[0].Id, $"{slug.ToUpperInvariant()}-1001", 2500m, "USD", due, "Consulting"),
            Invoice.Create(company.Id, contractors[1].Id, $"{slug.ToUpperInvariant()}-1002", 7200.50m, "USD", due, "Design work"),
            Invoice.Create(company.Id, contractors[2].Id, $"{slug.ToUpperInvariant()}-1003", 480m, "USD", due, "Copywriting"));
        return Task.CompletedTask;
    }
}
