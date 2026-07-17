namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// Ack returned from <c>RealtimeHub.SubscribeLobby</c>. Carries the lobby's current revision so the client can
/// immediately fetch the authorized snapshot (<c>GET /api/lobbies/{lobbyId}</c>) without waiting for a
/// <c>ResyncRequired</c> — subscribing never needs one.
/// </summary>
public sealed record SubscribeLobbyResultDto(int Revision);
