using System.Diagnostics;
using SimPle.Api.Observability;

namespace SimPle.Api.Middleware;

/// <summary>
/// Propagates a safe correlation token to application logs and the response. Untrusted or malformed input is replaced
/// rather than logged, preventing a request header from becoming a log-injection vector.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context.Request.Headers[HeaderName]);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("simple.correlation_id", correlationId);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using (_logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
            {
                await _next(context);
            }
        }
        finally
        {
            RequestTelemetry.Record(context, Stopwatch.GetElapsedTime(startedAt));
        }
    }

    private static string GetCorrelationId(Microsoft.Extensions.Primitives.StringValues suppliedValues)
    {
        if (suppliedValues.Count == 1 && IsSafeCorrelationId(suppliedValues[0]))
            return suppliedValues[0]!;

        return Activity.Current?.TraceId.ToString() is { Length: > 0 } traceId
            ? traceId
            : Guid.NewGuid().ToString("N");
    }

    private static bool IsSafeCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
                return false;
        }

        return true;
    }
}
