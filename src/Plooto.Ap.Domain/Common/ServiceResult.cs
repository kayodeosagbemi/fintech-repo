namespace Plooto.Ap.Domain.Common;

public enum ServiceOutcome
{
    Ok,
    /// <summary>The request had already been handled (same idempotency key); the original result is returned.</summary>
    Replayed,
    NotFound,
    Conflict,
    Invalid,
}

/// <summary>Expected business outcomes travel as values; exceptions are reserved for bugs and infrastructure faults.</summary>
public sealed record ServiceResult<T>(ServiceOutcome Outcome, T? Value, string? Error = null)
{
    public bool IsSuccess => Outcome is ServiceOutcome.Ok or ServiceOutcome.Replayed;

    public static ServiceResult<T> Ok(T value) => new(ServiceOutcome.Ok, value);
    public static ServiceResult<T> Replayed(T value) => new(ServiceOutcome.Replayed, value);
    public static ServiceResult<T> NotFound(string error) => new(ServiceOutcome.NotFound, default, error);
    public static ServiceResult<T> Conflict(string error) => new(ServiceOutcome.Conflict, default, error);
    public static ServiceResult<T> Invalid(string error) => new(ServiceOutcome.Invalid, default, error);
}
