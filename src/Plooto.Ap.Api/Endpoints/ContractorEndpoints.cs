using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;

namespace Plooto.Ap.Api.Endpoints;

internal static class ContractorEndpoints
{
    private const int MaxPageSize = 200;

    public static void MapContractorEndpoints(this RouteGroupBuilder scoped)
    {
        var g = scoped.MapGroup("/contractors");

        g.MapPost("/", async (Guid companyId, CreateContractorRequest request, IContractorRepository repo, CancellationToken ct) =>
        {
            var contractor = Contractor.Create(companyId, request.Name, request.Email, request.PayoutAccountRef);
            await repo.CreateAsync(contractor, ct);
            return Results.Created($"{ApiRoutes.Company(companyId)}/contractors/{contractor.Id}", contractor.ToResponse());
        });

        g.MapGet("/{contractorId:guid}", async (Guid companyId, Guid contractorId, IContractorRepository repo, CancellationToken ct) =>
        {
            var c = await repo.GetAsync(companyId, contractorId, ct);
            return c is null ? Http.NotFound("Contractor") : Http.Json(c.ToResponse(), StatusCodes.Status200OK);
        });

        g.MapGet("/", async (Guid companyId, IContractorRepository repo, CancellationToken ct,
            int? page, int? pageSize, string? email) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);
            var skip = (Math.Max(page ?? 1, 1) - 1) * size;
            var list = await repo.ListAsync(companyId, skip, size, email, ct);
            return Http.Json(list.Select(c => c.ToResponse()).ToList(), StatusCodes.Status200OK);
        });

        g.MapPut("/{contractorId:guid}", async (Guid companyId, Guid contractorId, UpdateContractorRequest request,
            IContractorRepository repo, CancellationToken ct) =>
        {
            var c = await repo.GetAsync(companyId, contractorId, ct);
            if (c is null) return Http.NotFound("Contractor");
            c.Update(request.Name, request.Email, request.PayoutAccountRef);
            await repo.UpdateAsync(c, ct);
            return Http.Json(c.ToResponse(), StatusCodes.Status200OK);
        });

        // Soft delete only: payments and invoices keep pointing at the contractor.
        g.MapDelete("/{contractorId:guid}", async (Guid companyId, Guid contractorId, IContractorRepository repo, CancellationToken ct) =>
            await repo.DeactivateAsync(companyId, contractorId, ct) ? Results.NoContent() : Http.NotFound("Contractor"));
    }
}
