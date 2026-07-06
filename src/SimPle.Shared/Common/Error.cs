namespace SimPle.Shared.Common;

public sealed record Error(string Code, string Message)
{
    /// <summary>
    /// Optional UTC instant a retryable failure (cooldown / rate limit) may be re-attempted. Surfaced in the
    /// error body and mirrored into the HTTP <c>Retry-After</c> header by the API layer.
    /// </summary>
    public DateTime? RetryAfterUtc { get; init; }

    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error Unauthorized = new("Auth.Unauthorized", "You are not authorized to perform this action.");
    public static readonly Error NotFound = new("General.NotFound", "The requested resource was not found.");
    public static readonly Error Conflict = new("General.Conflict", "A conflict occurred with the current state.");
    public static readonly Error Validation = new("General.Validation", "One or more validation errors occurred.");
}
