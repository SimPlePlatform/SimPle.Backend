using SimPle.Domain.Common;

namespace SimPle.Domain.Matchmaking;

/// <summary>
/// Binds one ticket to one proposed match request. <c>Active -&gt; Superseded|Failed</c>.
///
/// This row — specifically the partial unique index <c>UNIQUE (TicketId) WHERE State = 'Active'</c> — is the
/// correctness boundary that makes double-assignment impossible. <c>FOR UPDATE SKIP LOCKED</c> only stops two
/// workers from <em>contending</em> on the same row; a requeued ticket or a serialization retry can still attempt a
/// second assignment, and it is the index, not the row lock, that rejects it (Risk #1). The two-worker real-Postgres
/// test asserts zero duplicates against exactly this.
///
/// <see cref="GroupId"/> ties together the tickets of one proposal, so a group of any supported size shares a
/// single match request.
/// </summary>
public class MatchmakingAssignment : Entity
{
    public Guid TicketId { get; private set; }

    /// <summary>The M8 match request this assignment hands off to. Echoed back on MatchCreated/MatchCreationFailed.</summary>
    public Guid MatchRequestId { get; private set; }

    /// <summary>Shared by every ticket in the same proposal.</summary>
    public Guid GroupId { get; private set; }

    public MatchmakingAssignmentState State { get; private set; } = MatchmakingAssignmentState.Active;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }

    private MatchmakingAssignment() { }

    public static MatchmakingAssignment Create(
        Guid ticketId,
        Guid matchRequestId,
        Guid groupId,
        DateTime nowUtc)
    {
        if (ticketId == Guid.Empty)
            throw new ArgumentException("TicketId must not be empty.", nameof(ticketId));
        if (matchRequestId == Guid.Empty)
            throw new ArgumentException("MatchRequestId must not be empty.", nameof(matchRequestId));
        if (groupId == Guid.Empty)
            throw new ArgumentException("GroupId must not be empty.", nameof(groupId));

        return new MatchmakingAssignment
        {
            TicketId = ticketId,
            MatchRequestId = matchRequestId,
            GroupId = groupId,
            State = MatchmakingAssignmentState.Active,
            CreatedAtUtc = nowUtc,
        };
    }

    public bool IsActive => State == MatchmakingAssignmentState.Active;

    /// <summary>
    /// Stood down so the ticket may be assigned again (e.g. its group's handoff was requeued). Releasing the active
    /// slot is what lets the partial unique index accept a fresh assignment for the same ticket.
    /// </summary>
    public MatchmakingOutcome Supersede(DateTime nowUtc)
    {
        if (!IsActive) return MatchmakingOutcome.InvalidTransition;

        State = MatchmakingAssignmentState.Superseded;
        ResolvedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    public MatchmakingOutcome MarkFailed(DateTime nowUtc)
    {
        if (!IsActive) return MatchmakingOutcome.InvalidTransition;

        State = MatchmakingAssignmentState.Failed;
        ResolvedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }
}
