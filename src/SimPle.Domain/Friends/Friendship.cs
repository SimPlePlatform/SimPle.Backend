using SimPle.Domain.Common;

namespace SimPle.Domain.Friends;

/// <summary>
/// One row per unordered user pair for its lifetime (see the unique LEAST/GREATEST expression index).
/// The current <see cref="Status"/> is the live state; history fields retain the last transition's
/// actor, reason and timestamps. <see cref="DomainVersion"/> is a monotonic per-relationship counter
/// stamped on the outbox event of each transition; <see cref="RequestCycleId"/> increments each time a
/// terminal row is reactivated by a fresh request, so a later request cannot collide with an earlier
/// dismissed downstream notification. <see cref="Version"/> (xmin) is the optimistic-concurrency token.
/// </summary>
public class Friendship : Entity
{
    public Guid RequesterId { get; private set; }
    public Guid AddresseeId { get; private set; }
    public FriendshipStatus Status { get; private set; } = FriendshipStatus.Pending;
    public DateTime SentAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public RelationshipEndReason? EndReason { get; private set; }

    /// <summary>Who caused the last state transition (never taken from a request body).</summary>
    public Guid? TransitionActorId { get; private set; }

    /// <summary>Cooldown gate: the constrained requester may not re-request before this UTC instant.</summary>
    public DateTime? NextRequestAllowedAt { get; private set; }

    /// <summary>Monotonic per-relationship version stamped on each emitted event. Starts at 1.</summary>
    public long DomainVersion { get; private set; } = 1;

    /// <summary>Incremented on each new request cycle after a terminal transition. Starts at 1.</summary>
    public int RequestCycleId { get; private set; } = 1;

    public uint Version { get; private set; }   // mapped to xmin via IsRowVersion() in EF config

    private Friendship() { }

    public static Friendship Request(Guid requesterId, Guid addresseeId) => new()
    {
        RequesterId = requesterId,
        AddresseeId = addresseeId,
        Status = FriendshipStatus.Pending,
        SentAt = DateTime.UtcNow,
        TransitionActorId = requesterId,
        DomainVersion = 1,
        RequestCycleId = 1,
    };

    public void Accept(Guid actorId)
    {
        Status = FriendshipStatus.Accepted;
        AcceptedAt = DateTime.UtcNow;
        EndedAt = null;
        EndReason = null;
        NextRequestAllowedAt = null;
        Transition(actorId);
    }

    /// <param name="nextRequestAllowedAt">Requester decline cooldown (7 UTC days) end instant.</param>
    public void Decline(Guid actorId, DateTime nextRequestAllowedAt)
    {
        Status = FriendshipStatus.Declined;
        EndedAt = DateTime.UtcNow;
        NextRequestAllowedAt = nextRequestAllowedAt;
        Transition(actorId);
    }

    /// <param name="nextRequestAllowedAt">Requester cancel cooldown (24h) end instant.</param>
    public void Cancel(Guid actorId, DateTime nextRequestAllowedAt)
    {
        Status = FriendshipStatus.Cancelled;
        EndedAt = DateTime.UtcNow;
        NextRequestAllowedAt = nextRequestAllowedAt;
        Transition(actorId);
    }

    /// <summary>An accepted participant removes the friendship: terminal Cancelled with reason Removed.</summary>
    public void Remove(Guid actorId)
    {
        Status = FriendshipStatus.Cancelled;
        EndedAt = DateTime.UtcNow;
        EndReason = RelationshipEndReason.Removed;
        Transition(actorId);
    }

    /// <summary>A committed block ends any pending/accepted edge in the same transaction.</summary>
    public void EndByBlock(Guid actorId)
    {
        Status = FriendshipStatus.Cancelled;
        EndedAt = DateTime.UtcNow;
        EndReason = RelationshipEndReason.Blocked;
        Transition(actorId);
    }

    /// <summary>
    /// Reactivates a terminal row on the same DB row for a fresh request cycle. SentAt is the new request
    /// time; CreatedAt is unchanged; RequestCycleId increments; prior end/cooldown history is cleared.
    /// </summary>
    public void Reactivate(Guid requesterId, Guid addresseeId)
    {
        RequesterId = requesterId;
        AddresseeId = addresseeId;
        Status = FriendshipStatus.Pending;
        SentAt = DateTime.UtcNow;
        AcceptedAt = null;
        EndedAt = null;
        EndReason = null;
        NextRequestAllowedAt = null;
        RequestCycleId += 1;
        Transition(requesterId);
    }

    private void Transition(Guid actorId)
    {
        TransitionActorId = actorId;
        DomainVersion += 1;
        Touch();
    }
}

public class Block : Entity
{
    public Guid BlockerId { get; private set; }
    public Guid BlockedId { get; private set; }

    private Block() { }

    public static Block Create(Guid blockerId, Guid blockedId) => new()
    {
        BlockerId = blockerId,
        BlockedId = blockedId,
    };
}

public class UserFriendSettings : Entity
{
    public Guid UserId { get; private set; }
    public FriendRequestPrivacy FriendRequestPrivacy { get; private set; } = FriendRequestPrivacy.Anyone;

    private UserFriendSettings() { }

    public static UserFriendSettings CreateDefault(Guid userId) => new()
    {
        UserId = userId,
        FriendRequestPrivacy = FriendRequestPrivacy.Anyone,
    };

    public void UpdatePrivacy(FriendRequestPrivacy privacy) { FriendRequestPrivacy = privacy; Touch(); }
}

public enum FriendshipStatus { Pending, Accepted, Declined, Cancelled }

public enum FriendRequestPrivacy { Anyone, FriendsOfFriends, Off }

/// <summary>Why an accepted/pending edge was force-ended (distinct from Declined/Cancelled states).</summary>
public enum RelationshipEndReason { Removed, Blocked }
