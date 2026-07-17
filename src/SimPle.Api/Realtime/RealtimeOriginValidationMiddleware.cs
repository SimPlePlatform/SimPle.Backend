using Microsoft.Extensions.Options;
using SimPle.Api.Models;

namespace SimPle.Api.Realtime;

/// <summary>
/// Rejects any request to the realtime hub whose <c>Origin</c> header is not an exact match against the
/// configured allowlist (<see cref="RealtimeOptions.AllowedOrigins"/>). Runs ahead of SignalR's own request
/// handling for both the negotiate call and the WebSocket upgrade — WebSocket upgrades are not subject to browser
/// CORS enforcement, so this check is the actual security boundary, not <c>UseCors</c>.
/// </summary>
public sealed class RealtimeOriginValidationMiddleware
{
    public const string HubPathPrefix = "/hubs/realtime";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<RealtimeOptions> _options;

    public RealtimeOriginValidationMiddleware(RequestDelegate next, IOptionsMonitor<RealtimeOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(HubPathPrefix))
        {
            var origin = context.Request.Headers.Origin.ToString();
            var allowedOrigins = _options.CurrentValue.AllowedOrigins;

            // M07-003: a missing Origin header is rejected, not waved through. Real browsers always send Origin
            // on the negotiate POST and the WebSocket upgrade — same-origin or not, unsafe-method fetches carry
            // it per the Fetch spec — so the only client that reaches this path with no Origin at all is a
            // non-browser tool forging the request, exactly the actor this check exists to stop. A
            // present-but-unlisted Origin remains rejected too.
            if (string.IsNullOrEmpty(origin) || !allowedOrigins.Contains(origin, StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                    new ApiErrorDetail("Realtime.OriginRejected", "Origin not allowed for the realtime hub.")));
                return;
            }
        }

        await _next(context);
    }
}
