namespace SimPle.Application.Games.DTOs;

/// <summary>
/// One entry-point action on a catalog/detail DTO. In M4 every action is <c>deferred</c> — no engine exists
/// yet — and <see cref="OwnerModule"/> names the module that will flip it to <c>enabled</c> after its own
/// backend and E2E gate pass. Never mutated per-request; the same fixed set is projected for every game.
/// </summary>
public sealed record GameEntryActionDto(string Action, string Status, string ReasonCode, int OwnerModule);
