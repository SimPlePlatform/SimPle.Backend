using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimPle.Api.Observability;

/// <summary>
/// Low-cardinality request measurements. Exporters are intentionally configured by the deployment environment so
/// telemetry endpoints and credentials never need to be committed to the application repository.
/// </summary>
public static class RequestTelemetry
{
    public static readonly Meter Meter = new("SimPle.Api", "1.0.0");

    private static readonly Counter<long> RequestCount = Meter.CreateCounter<long>("simple.http.server.requests");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("simple.http.server.duration", "ms");

    public static void Record(HttpContext context, TimeSpan duration)
    {
        // Do not attach a path, user ID, correlation ID, or exception message: those cause high-cardinality or
        // sensitive telemetry. An observability backend can safely aggregate these tags for alerting.
        var tags = new TagList
        {
            { "http.request.method", context.Request.Method },
            { "http.response.status_code", context.Response.StatusCode }
        };

        RequestCount.Add(1, tags);
        RequestDuration.Record(duration.TotalMilliseconds, tags);
    }
}
