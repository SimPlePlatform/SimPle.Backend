namespace SimPle.Application.Realtime.Authorization;

/// <summary>
/// The action being authorized against a realtime scope (docs/specs/module-07-realtime-presence-chat-spec.md,
/// "Authorization / Privacy rules"). B1 only ever calls <see cref="Subscribe"/> (via <c>SubscribeLobby</c>); the
/// authorizer must still implement <see cref="Send"/>/<see cref="Delete"/> now because B2's chat commands will
/// call them and the authorization surface must not change shape between backend sessions.
/// </summary>
public enum RealtimeAction
{
    Subscribe,
    Send,
    Delete,
}
