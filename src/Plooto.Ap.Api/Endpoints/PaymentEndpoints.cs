using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Middleware;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Services.Payment;

namespace Plooto.Ap.Api.Endpoints;

internal static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this RouteGroupBuilder scoped)
    {
        var g = scoped.MapGroup("/payments");

        g.MapPost("/", async (Guid companyId, CreatePaymentRequest request, HttpContext http, PaymentOrchestrator orchestrator, CancellationToken ct) =>
        {
            var key = http.Request.Headers[HeaderNames.IdempotencyKey].ToString();
            var result = await orchestrator.CreateAsync(companyId, request.InvoiceId, key, PaymentMethod.Single, null, ct);
            if (!result.IsSuccess) return Http.Error(Http.StatusFor(result.Outcome), result.Error!);

            var status = Http.StatusFor(result.Outcome);   // 201 first time, 200 on replay
            if (status == StatusCodes.Status201Created)
                http.Response.Headers.Location = $"{ApiRoutes.Company(companyId)}/payments/{result.Value!.Id}";
            return Http.Json(result.Value!.ToResponse(), status);
        }).AddEndpointFilter<IdempotencyFilter>();

        g.MapPost("/batch", async (Guid companyId, CreateBatchPaymentRequest request, HttpContext http, PaymentOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (request.Items is null || request.Items.Count == 0)
                return Http.Error(StatusCodes.Status400BadRequest, "A batch needs at least one item.");
            if (request.Items.Count > PaymentOrchestrator.MaxBatchSize)
                return Http.Error(StatusCodes.Status400BadRequest, $"A batch can contain at most {PaymentOrchestrator.MaxBatchSize} items.");

            var key = http.Request.Headers[HeaderNames.IdempotencyKey].ToString();
            var response = await orchestrator.CreateBatchAsync(companyId, request.Items.Select(i => i.InvoiceId).ToList(), key, ct);
            return Http.Json(response, StatusCodes.Status207MultiStatus);
        }).AddEndpointFilter<IdempotencyFilter>();

        g.MapGet("/{paymentId:guid}", async (Guid companyId, Guid paymentId, IPaymentRepository payments, IInvoiceRepository invoices,
            IContractorRepository contractors, IApprovalRepository approvals, CancellationToken ct) =>
        {
            var payment = await payments.GetAsync(companyId, paymentId, ct);
            if (payment is null) return Http.NotFound("Payment");
            var invoice = await invoices.GetAsync(companyId, payment.InvoiceId, ct);
            var contractor = await contractors.GetAsync(companyId, payment.ContractorId, ct);
            var approval = await approvals.GetByPaymentAsync(companyId, payment.Id, ct);
            return Http.Json(new PaymentDetailResponse(payment.ToResponse(), invoice?.ToResponse(), contractor?.ToResponse(),
                approval?.Id, approval?.Status.ToString()), StatusCodes.Status200OK);
        });

        g.MapGet("/", async (Guid companyId, IPaymentRepository payments, CancellationToken ct,
            string? status, Guid? contractorId, Guid? batchId, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            if (batchId is { } b)
                return Http.Json((await payments.ListByBatchAsync(companyId, b, ct)).Select(p => p.ToResponse()).ToList(), StatusCodes.Status200OK);

            PaymentStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var s))
                    return Http.Error(StatusCodes.Status400BadRequest, $"Unknown payment status '{status}'.");
                parsed = s;
            }
            var list = await payments.ListAsync(companyId, parsed, contractorId, from, to, ct);
            return Http.Json(list.Select(p => p.ToResponse()).ToList(), StatusCodes.Status200OK);
        });
    }

    public static void MapApprovalEndpoints(this RouteGroupBuilder scoped)
    {
        var g = scoped.MapGroup("/approvals");

        g.MapGet("/", async (Guid companyId, IApprovalRepository approvals, CancellationToken ct, string? status) =>
        {
            ApprovalStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var s))
                    return Http.Error(StatusCodes.Status400BadRequest, $"Unknown approval status '{status}'.");
                parsed = s;
            }
            var list = await approvals.ListAsync(companyId, parsed, ct);
            return Http.Json(list.Select(a => a.ToResponse()).ToList(), StatusCodes.Status200OK);
        });

        // Stand-in for the diagram's Approver Service/UI: a human or script calls this directly.
        g.MapPost("/{approvalId:guid}/decision", async (Guid companyId, Guid approvalId, ResolveApprovalRequest request,
            IPaymentService payments, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ApprovalDecision>(request.Decision, ignoreCase: true, out var decision))
                return Http.Error(StatusCodes.Status400BadRequest, "Decision must be 'Approve' or 'Reject'.");

            var result = await payments.ResolveApprovalAsync(companyId, approvalId, decision, request.ApproverEmail, ct);
            return result.IsSuccess
                ? Http.Json(result.Value!.ToResponse(), StatusCodes.Status200OK)
                : Http.Error(Http.StatusFor(result.Outcome), result.Error!);
        });
    }

    public static void MapStatementEndpoint(this RouteGroupBuilder scoped)
    {
        scoped.MapGet("/statement", async (Guid companyId, IStatementRepository statements, CancellationToken ct,
            DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var lines = await statements.ListAsync(companyId, from, to, ct);

            // Payment lines debit the balance; Failure lines are informational and move nothing.
            decimal running = 0m, totalPaid = 0m;
            var rows = new List<StatementLineResponse>(lines.Count);
            foreach (var l in lines)
            {
                var signed = l.Type == StatementLineTypes.Payment ? -l.Amount : 0m;
                running += signed;
                if (l.Type == StatementLineTypes.Payment) totalPaid += l.Amount;
                rows.Add(new StatementLineResponse(l.OccurredAt, l.Type, l.CounterParty, signed, l.Currency, l.PaymentId, running));
            }
            return Http.Json(new StatementResponse(companyId, from, to, rows, totalPaid,
                lines.Count(l => l.Type == StatementLineTypes.Payment)), StatusCodes.Status200OK);
        });
    }
}
