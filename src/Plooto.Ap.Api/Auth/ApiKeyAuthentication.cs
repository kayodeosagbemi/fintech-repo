using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Infrastructure.Security;

namespace Plooto.Ap.Api.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string CompanyIdClaim = "company_id";
}

/// <summary>
/// Deliberately minimal, local-only scheme (README: "simple token-based authentication for demonstration purposes"):
/// each company holds one static API key, sent as `Authorization: Bearer &lt;key&gt;`. Only the key's SHA-256 is stored.
/// Not OAuth/OIDC; a production system would use short-lived tokens.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICompanyRepository companies)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return AuthenticateResult.NoResult();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Authorization header must use the Bearer scheme.");

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.Fail("Empty API key.");

        var company = await companies.GetByApiKeyHashAsync(ApiKey.Hash(token), Context.RequestAborted);
        if (company is null)
            return AuthenticateResult.Fail("Invalid API key.");

        var identity = new ClaimsIdentity(
            [new Claim(ApiKeyAuthenticationOptions.CompanyIdClaim, company.Id.ToString()), new Claim(ClaimTypes.Name, company.Slug)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Http.Error(StatusCodes.Status401Unauthorized, "A valid API key is required (Authorization: Bearer <api-key>).")
            .ExecuteAsync(Context);
    }
}

/// <summary>
/// Authorization, not just authentication: the route's {companyId} must equal the token's company. This is the HTTP-level
/// half of tenant isolation (the other half is every repository query taking an explicit companyId). 403, never 404, so
/// the response doesn't depend on whether the other company's resource exists.
/// </summary>
public sealed class CompanyScopeFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var routeCompanyId = context.HttpContext.Request.RouteValues["companyId"]?.ToString();
        var tokenCompanyId = context.HttpContext.User.FindFirst(ApiKeyAuthenticationOptions.CompanyIdClaim)?.Value;
        if (routeCompanyId is null || !string.Equals(routeCompanyId, tokenCompanyId, StringComparison.OrdinalIgnoreCase))
            return Http.Error(StatusCodes.Status403Forbidden, "This API key does not grant access to that company.");
        return await next(context);
    }
}
