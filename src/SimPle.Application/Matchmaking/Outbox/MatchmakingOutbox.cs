using System.Text.Json;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Matchmaking.Outbox;

/// <summary>
/// Module 6's matchmaking integration events, mirroring <see cref="SimPle.Application.Lobbies.Outbox.LobbyOutbox"/>.
/// Ids only — no rating, no region, no profile snapshot.
///
/// <para>
/// A matched proposal emits <strong>exactly one</strong> <c>MatchRequestedV1</c> for the whole group, not one per
/// ticket. The group is the thing M8 creates a match from; N events would either make M8 build N matches for one
/// proposal or force it to de-duplicate them itself.
/// </para>
///
/// <para>
/// The aggregate is the <em>group</em>, and <c>AggregateDomainVersion</c> is pinned to 1 because a group is created
/// once and never transitions. That makes the outbox's unique <c>(AggregateId, EventType, AggregateDomainVersion)</c>
/// index a real idempotency guard here: if a worker's transaction is retried and re-stages the same group's event,
/// the index rejects the duplicate rather than handing M8 two requests for one proposal.
/// </para>
///
/// <para>
/// It is the same <c>MatchRequestedV1</c> event type the lobby Start path emits, deliberately: M8 has one consumer
/// for "somebody wants a match", and the payload's <c>source</c> tells it whether the request came from a lobby or
/// the queue. A committed row is a durable <em>request</em>, never a created match (Risk #6).
/// </para>
/// </summary>
public static class MatchmakingOutbox
{
    public const int EventVersion = 1;

    private const string GroupAggregate = "MatchmakingGroup";

    /// <summary>The same wire name the lobby Start path uses — one M8 consumer, two producers.</summary>
    public const string MatchRequested = "MatchRequestedV1";

    /// <summary>Distinguishes a queue-originated request from a lobby-originated one, for M8's benefit.</summary>
    public const string QueueSource = "matchmaking";

    public static OutboxMessage MatchRequestedEvent(
        Guid groupId,
        Guid matchRequestId,
        string gameSlug,
        int capabilityVersion,
        IReadOnlyList<Guid> ticketIds) =>
        OutboxMessage.Create(
            GroupAggregate, groupId, MatchRequested, EventVersion,
            aggregateDomainVersion: 1, requestCycleId: 1,
            JsonSerializer.Serialize(new
            {
                source = QueueSource,
                matchRequestId,
                groupId,
                gameSlug,
                capabilityVersion,
                // Ticket ids, not user ids: M8 re-reads the tickets it names rather than trusting a roster snapshot
                // that could already be stale by the time the event is delivered.
                ticketIds,
            }));
}
