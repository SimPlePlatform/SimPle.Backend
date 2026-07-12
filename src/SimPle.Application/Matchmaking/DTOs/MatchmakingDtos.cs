using SimPle.Application.Lobbies.DTOs;

namespace SimPle.Application.Matchmaking.DTOs;

/// <summary>
/// Enqueue a Quick Match ticket.
///
/// There is deliberately no <c>userId</c> and no <c>rating</c>: the actor is always the JWT <c>sub</c> claim, and
/// the rating is a server-side snapshot. A client-supplied rating would let a caller pick their own opponents.
///
/// There is also no idempotency key, and that is not an omission — the approved spec's data model gives
/// <c>MatchmakingTicket</c> no such column (only <c>LobbyStartRequest</c> has one). A retried enqueue is instead
/// made safe by the pool key itself: re-enqueueing the identical ticket returns the live one rather than minting a
/// second (see <c>MatchmakingService.EnqueueAsync</c>), which is the same idempotency a double-submitted join gets.
/// </summary>
public sealed record CreateTicketRequestDto(
    string GameSlug,
    int CapabilityVersion,
    string Mode,
    int PlayerCount,
    string TimeControlId,
    bool Rated,
    string? Region);

/// <summary>
/// A ticket as its owner sees it. Polled every 2 seconds until Module 7 supplies live delivery.
///
/// <paramref name="CurrentBand"/> is the rating half-width the ticket will accept <em>right now</em> (±100 → ±200 →
/// ±400 as it ages), so the queue UI can show a search visibly widening instead of an opaque spinner. It is null
/// once the ticket has reached its deadline — an expired ticket has no band, it has a terminal outcome.
///
/// <paramref name="DependencyReadiness"/> is the honest part. Until Module 8 registers a match runtime, the matching
/// worker does not run at all (a committed assignment with nobody to consume it would mark a ticket <c>Matched</c>
/// and send the player to a room that cannot exist — precisely the fabrication Risk #6 forbids). So a Phase-1
/// ticket queues, widens, and honestly times out. The client reads that from the probe rather than hardcoding it.
/// </summary>
public sealed record TicketDto(
    Guid TicketId,
    string GameSlug,
    int CapabilityVersion,
    string Mode,
    int PlayerCount,
    string TimeControlId,
    bool Rated,
    string ResolvedRegion,
    int Rating,
    string RatingSourceVersion,
    string State,
    DateTime EnqueuedAtUtc,
    DateTime DeadlineAtUtc,
    int? CurrentBand,
    TicketAssignmentDto? Assignment,
    DependencyReadinessDto DependencyReadiness);

/// <summary>
/// The handoff a matched ticket points at. A <see cref="MatchRequestId"/> is a durable <em>request</em> — never a
/// created match (Risk #6). Nothing in Module 6 may navigate a client to a room on the strength of it; only
/// Module 8's <c>MatchCreatedV1</c> can do that.
/// </summary>
public sealed record TicketAssignmentDto(
    Guid MatchRequestId,
    Guid GroupId);

/// <summary>
/// Module 6's matchmaking error catalogue, in the codebase's PascalCase house style (reconciliation
/// <strong>R1</strong>). The lobby half lives in <c>LobbyErrors</c>; enqueue reuses it for the errors the two
/// genuinely share (capability, blocks, concurrency, validation) rather than minting parallel codes that mean the
/// same thing.
/// </summary>
public static class MatchmakingErrors
{
    /// <summary>
    /// <strong>Privacy-safe.</strong> Another user's ticket id lands here, not on a 403 — a 403 would confirm the
    /// id exists (OWASP API1:2023). Missing and foreign ticket ids are indistinguishable.
    /// </summary>
    public const string TicketNotFound = "Matchmaking.TicketNotFound";

    /// <summary>The one-active-lobby-<em>or</em>-ticket invariant, from the queue's side.</summary>
    public const string AlreadyQueued = "Matchmaking.AlreadyQueued";

    public const string TicketExpired = "Matchmaking.TicketExpired";

    /// <summary>
    /// Queue <em>execution</em> is disabled while no match runtime is registered.
    ///
    /// Deliberately <strong>not</strong> returned by enqueue: the spec requires ticket create/status/cancel and
    /// expiry to remain fully functional without M8, and they do. It becomes reachable when M8 exists and its
    /// handoff can fail — the constant is defined now so the frontend's error mapping does not have to change then.
    /// </summary>
    public const string RuntimeUnavailable = "Matchmaking.RuntimeUnavailable";
}
