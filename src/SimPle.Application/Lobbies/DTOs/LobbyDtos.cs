using SimPle.Shared.Common;

namespace SimPle.Application.Lobbies.DTOs;

/// <summary>
/// The full lobby view for a member or an authorized viewer.
///
/// It deliberately carries no credential. The join code and link token appear in exactly one place —
/// <see cref="LobbyCredentialDto"/>, returned only by create and rotate — so there is no read path that could
/// hand a private code to someone who merely holds the lobby id (Risk #7).
///
/// <paramref name="AllowedActions"/> and <paramref name="DependencyReadiness"/> exist so the client can render
/// honest enabled/disabled controls without re-deriving host role, lifecycle, or which later modules are live.
/// A client that guessed would eventually guess wrong and offer an action the server rejects.
/// </summary>
public sealed record LobbyDto(
    Guid LobbyId,
    string GameSlug,
    int CapabilityVersion,
    string Privacy,
    int MaxPlayers,
    string TimeControlId,
    bool Rated,
    string ResolvedRegion,
    string SpectatorPolicy,
    string TieBreakRuleId,
    bool AiFillRequested,
    string State,
    int Revision,
    DateTime ExpiresAtUtc,
    string? ClosedReason,
    Guid HostUserId,
    IReadOnlyList<LobbySeatDto> Seats,
    IReadOnlyList<string> AllowedActions,
    DependencyReadinessDto DependencyReadiness);

/// <summary>
/// One seat. Identity is the canonical shared <see cref="PublicIdentityDto"/> — the same shape M3 search and
/// friend lists use — so a member avatar/name always links to the real profile and a lobby never invents its own
/// identity fields.
/// </summary>
public sealed record LobbySeatDto(
    PublicIdentityDto Identity,
    bool IsHost,
    bool IsReady,
    DateTime JoinedAtUtc);

/// <summary>
/// Which downstream modules are actually live. Every value is <c>false</c> in Module 6 and is <em>read from the
/// real probe</em>, never hardcoded in the client — so when M7/M8/M9 land, the UI turns on without a frontend
/// change, and until then it cannot claim an action that would fail.
/// </summary>
public sealed record DependencyReadinessDto(
    bool Chat,
    bool MatchRuntime,
    bool AiParticipants);

/// <summary>
/// The plaintext join credential. Returned <em>only</em> from create and rotate, and never persisted in this
/// form — the database holds keyed digests alone. Rotating supersedes the previous value immediately.
/// </summary>
public sealed record LobbyCredentialDto(
    string Code,
    string LinkToken,
    int Generation,
    DateTime ExpiresAtUtc);

public sealed record CreateLobbyResultDto(
    LobbyDto Lobby,
    LobbyCredentialDto Credential);

/// <summary>
/// A public-discovery row. Strictly narrower than <see cref="LobbyDto"/>: no seat roster, no readiness, no
/// credential — a lobby you have not joined tells you only what you need in order to decide to join it.
/// Private lobbies never appear here at all.
/// </summary>
public sealed record LobbySummaryDto(
    Guid LobbyId,
    string GameSlug,
    int MaxPlayers,
    int JoinedCount,
    string TimeControlId,
    bool Rated,
    string ResolvedRegion,
    string SpectatorPolicy,
    PublicIdentityDto Host,
    DateTime CreatedAt,
    DateTime ExpiresAtUtc);

public sealed record LobbyInviteDto(
    Guid InviteId,
    Guid LobbyId,
    string GameSlug,
    PublicIdentityDto Inviter,
    string State,
    DateTime ExpiresAtUtc);

/// <summary>
/// The dashboard's "what am I currently in" query. Exactly one of the two is ever set — that is the
/// one-active-lobby-<em>or</em>-ticket invariant, surfaced rather than re-derived by the client.
/// </summary>
public sealed record ActiveContextDto(
    LobbyDto? Lobby,
    Guid? TicketId);

/// <summary>
/// The outcome of a start attempt. Before M8 registers a consumer no start can succeed at all, so this type is
/// reachable only once M8 exists; the honest 503 (<c>Lobbies.MatchRuntimeUnavailable</c>) is what M6 returns
/// today. A committed <see cref="MatchRequestId"/> is a durable <em>request</em>, never a created match.
/// </summary>
public sealed record StartLobbyResultDto(
    Guid LobbyId,
    Guid MatchRequestId,
    string State,
    int Revision);

/// <summary>
/// The capability source (D2), read-only. Lets a client populate <c>capabilityVersion</c> and the allowed
/// modes/time controls/tie-break rules/spectator policies <em>before</em> calling create-lobby or create-ticket,
/// instead of guessing values the server will reject. Always the highest-versioned <c>IsActive</c> profile for the
/// slug — a lobby/ticket already in flight keeps meaning what it meant at creation via its own pinned version, so
/// this route is never used to reinterpret an existing lobby.
/// </summary>
public sealed record GameCapabilityProfileDto(
    string GameSlug,
    int CapabilityVersion,
    int MinPlayers,
    int MaxPlayers,
    IReadOnlyList<string> AllowedModes,
    IReadOnlyList<string> TimeControls,
    IReadOnlyList<string> TieBreakRules,
    IReadOnlyList<string> SpectatorPolicies,
    bool RatedEligible,
    bool AiFillEligible);

/// <summary>The action names surfaced in <see cref="LobbyDto.AllowedActions"/>.</summary>
public static class LobbyActions
{
    public const string Leave = "leave";
    public const string Ready = "ready";
    public const string Settings = "settings";
    public const string Invite = "invite";
    public const string Kick = "kick";
    public const string RotateCredential = "rotate-credential";
    public const string Start = "start";
}
