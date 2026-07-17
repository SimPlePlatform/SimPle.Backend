using SimPle.Shared.Common;

namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// Safe-fields-only chat message DTO (docs/specs/module-07-realtime-presence-chat-spec.md, "Response DTO").
/// <c>Sender</c> is the shared M3 player-identity contract, <see cref="PublicIdentityDto"/> — the same shape
/// <c>LobbySeatDto.Identity</c>/<c>Host</c>/<c>Inviter</c> already embed (see <c>LobbyDtos.cs</c>). Composing
/// modules must never clone an incompatible identity shape (<see cref="PublicIdentityDto"/>'s own doc comment).
/// A deleted, blocked, or hidden sender renders a non-navigable safe tombstone — never the real profile fields.
/// </summary>
public sealed record ChatMessageDto(
    Guid Id,
    Guid LobbyId,
    PublicIdentityDto Sender,
    string? Body,
    bool Deleted,
    DateTime CreatedAt,
    int SchemaVersion);
