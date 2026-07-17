namespace SimPle.Application.Realtime.Contracts;

/// <summary>Result of the hub's <c>SendLobbyMessage</c> method (docs/specs/module-07-realtime-presence-chat-spec.
/// md, API contract). Mirrors <see cref="SubscribeLobbyResultDto"/>'s shape.</summary>
public sealed record SendLobbyMessageResultDto(ChatMessageDto Message);
