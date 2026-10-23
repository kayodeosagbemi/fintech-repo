using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Domain.Common;

namespace Plooto.Ap.Api;

internal static class Http
{
    /// <summary>JSON result with an explicit status. (Results.Json exposes status and value, which the idempotency filter reads.)</summary>
    public static IResult Json<T>(T value, int status) => Results.Json(value, statusCode: status);

    public static IResult Error(int status, string message) => Json(new ErrorResponse(message), status);

    public static IResult NotFound(string what) => Error(StatusCodes.Status404NotFound, $"{what} was not found.");

    public static int StatusFor(ServiceOutcome outcome) => outcome switch
    {
        ServiceOutcome.Ok => StatusCodes.Status201Created,
        ServiceOutcome.Replayed => StatusCodes.Status200OK,
        ServiceOutcome.NotFound => StatusCodes.Status404NotFound,
        ServiceOutcome.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}
