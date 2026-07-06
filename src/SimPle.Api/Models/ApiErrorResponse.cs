namespace SimPle.Api.Models;

/// <summary>A safe API error response that does not expose internal exception detail.</summary>
/// <param name="Error">Machine-readable error code and user-safe message.</param>
public sealed record ApiErrorResponse(ApiErrorDetail Error);

/// <summary>Details for an API operation that could not be completed.</summary>
/// <param name="Code">Stable error identifier such as <c>Auth.InvalidCredentials</c>.</param>
/// <param name="Message">Safe message intended for an API client.</param>
/// <param name="RetryAfterUtc">When set (cooldown / rate limit), the UTC instant the client may retry.</param>
public sealed record ApiErrorDetail(string Code, string Message, DateTime? RetryAfterUtc = null);
