using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure.Repositories;

namespace Plooto.Ap.Api.Middleware;

/// <summary>
/// Makes the payment POSTs safe to retry (PLAN_1 section 2.4). The (CompanyId, Key) primary key is the real arbiter: an
/// atomic INSERT claims the key, so of N concurrent identical requests exactly one executes and the rest are told to
/// retry or are served the stored response. Only successful (2xx) responses are stored; a rejected request created
/// nothing, so releasing the key lets the client fix the problem and retry with the same key.
/// </summary>
public sealed class IdempotencyFilter(IIdempotencyStore store) : IEndpointFilter
{
    /// <summary>
    /// Batch item keys are "{key}:{invoiceId}" and must fit Payment.IdempotencyKey (200): 128 + 1 + 36 = 165.
    /// </summary>
    public const int MaxKeyLength = 128;
    /// <summary>An InProgress claim older than this is presumed orphaned by a dead process and may be taken over.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    /// <summary>HttpContext.Items key holding the trimmed, validated Idempotency-Key for the endpoint to use.</summary>
    public const string KeyItem = "idempotency:key";
    /// <summary>Set by an endpoint to ask the filter to release (not cache) an otherwise-2xx response.</summary>
    public const string SkipCacheItem = "idempotency:skip-cache";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;

        var key = http.Request.Headers[HeaderNames.IdempotencyKey].ToString().Trim();
        if (key.Length == 0)
            return Http.Error(StatusCodes.Status400BadRequest, $"The {HeaderNames.IdempotencyKey} header is required on this endpoint.");
        if (key.Length > MaxKeyLength)
            return Http.Error(StatusCodes.Status400BadRequest, $"{HeaderNames.IdempotencyKey} must be at most {MaxKeyLength} characters.");
        http.Items[KeyItem] = key;
        if (!Guid.TryParse(http.Request.RouteValues["companyId"]?.ToString(), out var companyId))
            return await next(context);

        var requestHash = HashRequest(http, context.Arguments);

        var claimed = await store.TryBeginAsync(companyId, key, requestHash, ct);
        if (!claimed)
        {
            var existing = await store.GetAsync(companyId, key, ct);
            if (existing is null)   // released between our failed insert and the read: let the client retry
                return RetryLater(http, "The request was racing another with the same Idempotency-Key; retry.");
            if (existing.RequestHash != requestHash)
                return Http.Error(StatusCodes.Status409Conflict, "Idempotency-Key was already used with a different request.");
            if (existing.Status == IdempotencyStatus.Completed)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Content(existing.ResponseBody, "application/json", Encoding.UTF8, existing.ResponseStatusCode);
            }
            // InProgress: if its owner died the record would block this key forever, so a stale one is taken over.
            // Safe because the orchestrator's replay-by-idempotency-key check prevents a double payment.
            var stale = DateTimeOffset.UtcNow - existing.CreatedAt >= StaleAfter
                && await store.TryTakeOverStaleAsync(companyId, key, requestHash, StaleAfter, ct);
            if (!stale)
                return RetryLater(http, "A request with this Idempotency-Key is still in progress.");
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            await store.AbandonAsync(companyId, key, CancellationToken.None);
            throw;
        }

        var skipCache = http.Items.TryGetValue(SkipCacheItem, out var skip) && skip is true;
        if (!skipCache && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 and var status } && result is IValueHttpResult { Value: { } value })
        {
            var body = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
            await store.CompleteAsync(companyId, key, status, body, CancellationToken.None);
        }
        else
        {
            await store.AbandonAsync(companyId, key, CancellationToken.None);
        }
        return result;
    }

    private static IResult RetryLater(HttpContext http, string message)
    {
        http.Response.Headers.RetryAfter = "1";
        return Http.Error(StatusCodes.Status409Conflict, message);
    }

    /// <summary>
    /// Hash of method + path + the bound request DTO re-serialized canonically, so whitespace or property order in the
    /// client's JSON never makes an identical retry look different.
    /// </summary>
    private static string HashRequest(HttpContext http, IList<object?> arguments)
    {
        var dto = arguments.OfType<IIdempotentRequest>().SingleOrDefault();
        var canonical = $"{http.Request.Method}|{http.Request.Path}|{(dto is null ? "" : JsonSerializer.Serialize(dto, dto.GetType(), JsonOptions))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
