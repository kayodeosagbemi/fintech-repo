using Plooto.Ap.Api.Auth;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Infrastructure.Security;

namespace Plooto.Ap.Api.Endpoints;

internal static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this IEndpointRouteBuilder app, RouteGroupBuilder scoped)
    {
        // Deliberate MVP shortcut: company creation is the one open endpoint, because it is how a caller obtains an API
        // key. A real deployment would gate this behind an admin credential or onboarding flow.
        app.MapPost(ApiRoutes.Companies, async (CreateCompanyRequest request, ICompanyRepository companies, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Http.Error(StatusCodes.Status400BadRequest, "Name is required.");

            var apiKey = ApiKey.Generate();
            var slug = $"{Slugify(request.Name)}-{Guid.NewGuid().ToString("N")[..6]}";
            var company = Company.Create(request.Name, slug, ApiKey.Hash(apiKey));
            await companies.CreateAsync(company, CompanyPreferences.CreateDefault(company.Id), ct);
            // The plaintext key is returned exactly once; only its hash is stored.
            return Results.Created(ApiRoutes.Company(company.Id), company.ToResponse(apiKey));
        }).AllowAnonymous();

        // "Who am I?": lets a client that only holds an API key discover its company id.
        app.MapGet(ApiRoutes.Me, async (HttpContext http, ICompanyRepository companies, CancellationToken ct) =>
        {
            var id = http.User.FindFirst(ApiKeyAuthenticationOptions.CompanyIdClaim)?.Value;
            var company = Guid.TryParse(id, out var companyId) ? await companies.GetAsync(companyId, ct) : null;
            return company is null ? Http.NotFound("Company") : Http.Json(company.ToResponse(), StatusCodes.Status200OK);
        }).RequireAuthorization();

        scoped.MapGet("/", async (Guid companyId, ICompanyRepository companies, CancellationToken ct) =>
        {
            var company = await companies.GetAsync(companyId, ct);
            return company is null ? Http.NotFound("Company") : Http.Json(company.ToResponse(), StatusCodes.Status200OK);
        });
    }

    public static void MapPreferencesEndpoints(this RouteGroupBuilder scoped)
    {
        scoped.MapGet("/preferences", async (Guid companyId, ICompanyRepository companies, CancellationToken ct) =>
        {
            var p = await companies.GetPreferencesAsync(companyId, ct);
            return p is null ? Http.NotFound("Company preferences") : Http.Json(ToResponse(p), StatusCodes.Status200OK);
        });

        scoped.MapPut("/preferences", async (Guid companyId, UpdateCompanyPreferencesRequest request, ICompanyRepository companies, CancellationToken ct) =>
        {
            var p = await companies.UpdatePreferencesAsync(companyId, request.DefaultCurrency, request.RequireApprovalAboveThreshold,
                request.ApprovalThreshold, request.PreferredPaymentRail, ct);
            return p is null ? Http.NotFound("Company preferences") : Http.Json(ToResponse(p), StatusCodes.Status200OK);
        });
    }

    private static CompanyPreferencesResponse ToResponse(CompanyPreferences p) =>
        new(p.DefaultCurrency, p.RequireApprovalAboveThreshold, p.ApprovalThreshold, p.PreferredPaymentRail);

    private static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "company" : slug[..Math.Min(slug.Length, 40)];
    }
}
