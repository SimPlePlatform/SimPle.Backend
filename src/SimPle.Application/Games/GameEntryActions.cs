using SimPle.Application.Games.DTOs;

namespace SimPle.Application.Games;

/// <summary>
/// The fixed set of entry-point actions projected onto every catalog/detail DTO. In M4 every action is
/// <c>deferred</c> — no game engine exists yet — per the spec's entry-actions table. A later module flips its
/// own action to <c>enabled</c> only after its backend and E2E gate pass; this list is never mutated per-request.
/// M6 (Lobby &amp; Matchmaking System) flipped its 3 owned actions after its backend + E2E gate passed; the
/// frontend still re-checks each enabled action against the specific game's capabilities/player count.
/// </summary>
public static class GameEntryActions
{
    public static readonly IReadOnlyList<GameEntryActionDto> All = new[]
    {
        new GameEntryActionDto("play-vs-ai", "deferred", "Games.EntryDeferred.AI", 9),
        new GameEntryActionDto("quick-match", "enabled", "Games.EntryDeferred.QuickMatch", 6),
        new GameEntryActionDto("create-lobby", "enabled", "Games.EntryDeferred.Lobby", 6),
        new GameEntryActionDto("invite-friend", "enabled", "Games.EntryDeferred.Invite", 6),
        new GameEntryActionDto("enter-match-room", "deferred", "Games.EntryDeferred.MatchRoom", 8),
    };
}
