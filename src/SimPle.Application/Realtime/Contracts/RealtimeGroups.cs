namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// SignalR group names are server-derived, never client-supplied — clients pass lobby ids only (never group
/// names, never lobby codes). Groups are a delivery optimization only; they are never checked for authorization
/// and never used as membership storage (docs/specs/module-07-realtime-presence-chat-spec.md).
/// </summary>
public static class RealtimeGroups
{
    public static string Lobby(Guid lobbyId) => $"lobby:{lobbyId:N}";
}
