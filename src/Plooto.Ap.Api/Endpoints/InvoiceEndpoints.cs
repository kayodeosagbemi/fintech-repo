using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;

namespace Plooto.Ap.Api.Endpoints;

internal static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this RouteGroupBuilder scoped)
    {
        var g = scoped.MapGroup("/invoices");

        g.MapPost("/", async (Guid companyId, CreateInvoiceRequest request, IInvoiceRepository invoices,
            IContractorRepository contractors, ICompanyRepository companies, CancellationToken ct) =>
        {
            var contractor = await contractors.GetAsync(companyId, request.ContractorId, ct);
            if (contractor is null || !contractor.IsActive) return Http.NotFound("Contractor");

            var currency = request.Currency;
            if (string.IsNullOrWhiteSpace(currency))
                currency = (await companies.GetPreferencesAsync(companyId, ct))?.DefaultCurrency ?? "USD";

            var invoice = Invoice.Create(companyId, contractor.Id, request.InvoiceNumber, request.Amount, currency,
                request.DueDate, request.Description);
            await invoices.CreateAsync(invoice, ct);
            return Results.Created($"{ApiRoutes.Company(companyId)}/invoices/{invoice.Id}", invoice.ToResponse());
        });

        g.MapGet("/{invoiceId:guid}", async (Guid companyId, Guid invoiceId, IInvoiceRepository invoices, CancellationToken ct) =>
        {
            var i = await invoices.GetAsync(companyId, invoiceId, ct);
            return i is null ? Http.NotFound("Invoice") : Http.Json(i.ToResponse(), StatusCodes.Status200OK);
        });

        g.MapGet("/", async (Guid companyId, IInvoiceRepository invoices, CancellationToken ct, string? status, Guid? contractorId) =>
        {
            InvoiceStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var s))
                    return Http.Error(StatusCodes.Status400BadRequest, $"Unknown invoice status '{status}'.");
                parsed = s;
            }
            var list = await invoices.ListAsync(companyId, parsed, contractorId, ct);
            return Http.Json(list.Select(i => i.ToResponse()).ToList(), StatusCodes.Status200OK);
        });

        // Editing an approved/paid invoice is an illegal transition: the domain throws and the error middleware returns 409.
        g.MapPut("/{invoiceId:guid}", async (Guid companyId, Guid invoiceId, UpdateInvoiceRequest request,
            IInvoiceRepository invoices, CancellationToken ct) =>
        {
            var invoice = await invoices.GetAsync(companyId, invoiceId, ct);
            if (invoice is null) return Http.NotFound("Invoice");
            invoice.Update(request.Amount, string.IsNullOrWhiteSpace(request.Currency) ? invoice.Currency : request.Currency,
                request.DueDate, request.Description);
            await invoices.UpdateAsync(invoice, ct);
            return Http.Json(invoice.ToResponse(), StatusCodes.Status200OK);
        });

        // Invoices are never hard-deleted: DELETE voids them (409 once paid).
        g.MapDelete("/{invoiceId:guid}", async (Guid companyId, Guid invoiceId, IInvoiceRepository invoices, CancellationToken ct) =>
        {
            var invoice = await invoices.GetAsync(companyId, invoiceId, ct);
            if (invoice is null) return Http.NotFound("Invoice");
            invoice.Void();
            await invoices.UpdateAsync(invoice, ct);
            return Results.NoContent();
        });
    }
}
