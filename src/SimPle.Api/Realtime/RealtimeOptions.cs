namespace SimPle.Api.Realtime;

/// <summary>
/// Config-driven exact-origin allowlist for the realtime hub handshake (docs/specs/module-07-realtime-presence-
/// chat-spec.md). Deliberately separate from the general <c>Cors:AllowedOrigin</c> policy: browsers do not apply
/// CORS to WebSocket upgrade requests, so the hub's origin check cannot rely on <c>UseCors</c> alone and must be
/// enforced explicitly, in application code, against an exact-match allowlist — never a wildcard.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}
