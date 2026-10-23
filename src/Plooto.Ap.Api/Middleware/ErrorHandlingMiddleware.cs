using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Api.Middleware;

/// <summary>
/// Maps domain/database failures that escape an endpoint to HTTP: invariant violations (ArgumentException) are the
/// caller's fault (400), illegal state transitions and uniqueness races are conflicts (409), anything else is a 500 with
/// no internal detail leaked.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly Regex ParameterSuffix = new(@"\s*\(Parameter '[^']*'\)\s*$", RegexOptions.Compiled);

    internal static string StripParameterSuffix(string message) => ParameterSuffix.Replace(message, "");

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
        {
            var (status, message) = ex switch
            {
                BadHttpRequestException => (StatusCodes.Status400BadRequest, "The request body is malformed or invalid."),
                ArgumentException a => (StatusCodes.Status400BadRequest, StripParameterSuffix(a.Message)),
                // Only domain rule violations carry caller-safe messages; any other InvalidOperationException is internal.
                DomainRuleException d => (StatusCodes.Status409Conflict, d.Message),
                DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource was modified concurrently; retry."),
                DbUpdateException d when DbErrors.IsUniqueViolation(d) => (StatusCodes.Status409Conflict, "A record with the same unique value already exists."),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
            };
            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            else
                logger.LogInformation("Request rejected ({Status}): {Message}", status, message);

            context.Response.Clear();
            await Http.Error(status, message).ExecuteAsync(context);
        }
    }
}
