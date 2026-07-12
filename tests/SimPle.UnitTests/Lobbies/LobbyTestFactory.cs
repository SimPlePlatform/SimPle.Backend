using SimPle.Domain.Capabilities;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Valid-by-default fixtures. Every factory takes an explicit <c>nowUtc</c> so a test can only ever build state
/// against its own fake clock — there is no overload that quietly reaches for <c>DateTime.UtcNow</c>.
/// </summary>
public static class LobbyTestFactory
{
    public static readonly DateTime T0 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    public static LobbySettings Settings(
        string gameSlug = "chess-lite",
        int capabilityVersion = 1,
        LobbyPrivacy privacy = LobbyPrivacy.Private,
        int maxPlayers = 4,
        string timeControlId = "blitz-3-2",
        bool rated = false,
        string resolvedRegion = "eu-west",
        SpectatorPolicy spectatorPolicy = SpectatorPolicy.Anyone,
        string tieBreakRuleId = "none",
        bool aiFillRequested = false) =>
        new(gameSlug, capabilityVersion, privacy, maxPlayers, timeControlId, rated,
            resolvedRegion, spectatorPolicy, tieBreakRuleId, aiFillRequested);

    public static Lobby Open(Guid hostUserId, DateTime nowUtc, LobbySettings? settings = null) =>
        Lobby.Create(hostUserId, settings ?? Settings(), Guid.NewGuid(), nowUtc);

    /// <summary>A lobby whose host and <paramref name="joiners"/> are all seated and ready.</summary>
    public static Lobby ReadyLobby(Guid hostUserId, DateTime nowUtc, params Guid[] joiners)
    {
        var lobby = Open(hostUserId, nowUtc);

        foreach (var joiner in joiners)
            lobby.Join(joiner, nowUtc);

        foreach (var joiner in joiners)
            lobby.SetReadiness(joiner, true, nowUtc);

        return lobby;
    }

    public static GameCapabilityProfile Profile(
        string gameSlug = "chess-lite",
        int capabilityVersion = 1,
        int minPlayers = 2,
        int maxPlayers = 4,
        IEnumerable<string>? allowedModes = null,
        IEnumerable<string>? timeControls = null,
        IEnumerable<string>? tieBreakRules = null,
        IEnumerable<string>? spectatorPolicies = null,
        bool ratedEligible = true,
        bool aiFillEligible = true) =>
        GameCapabilityProfile.Create(
            gameSlug,
            capabilityVersion,
            minPlayers,
            maxPlayers,
            allowedModes ?? new[] { "multiplayer", "ranked", "ai" },
            timeControls ?? new[] { "blitz-3-2", "rapid-10-0" },
            tieBreakRules ?? new[] { "none", "sudden-death" },
            spectatorPolicies ?? new[] { "Anyone", "FriendsOnly", "Disabled" },
            ratedEligible,
            aiFillEligible,
            "test-1");
}

public static class TicketFactory
{
    public static MatchmakingTicket Queued(
        DateTime nowUtc,
        Guid? userId = null,
        string gameSlug = "chess-lite",
        string mode = "multiplayer",
        int playerCount = 2,
        string timeControlId = "blitz-3-2",
        bool rated = false,
        string resolvedRegion = "eu-west",
        int rating = MatchmakingTicket.ProvisionalRating) =>
        MatchmakingTicket.Enqueue(
            userId ?? Guid.NewGuid(),
            gameSlug,
            capabilityVersion: 1,
            mode,
            playerCount,
            timeControlId,
            rated,
            resolvedRegion,
            rating,
            MatchmakingTicket.ProvisionalRatingSource,
            Guid.NewGuid(),
            nowUtc);
}
