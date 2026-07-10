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

    /// <summary>Who last sent (created/reactivated) this pair's request row — abuse-cap bookkeeping only.</summary>
    public Guid? LastSenderId { get; private set; }

    /// <summary>Sends by <see cref="LastSenderId"/> within the current <see cref="SendWindowStartUtc"/> window.</summary>
    public int SendCountInWindow { get; private set; }

    /// <summary>UTC start of the current send-count window; resets when the sender flips or the window elapses.</summary>
    public DateTime? SendWindowStartUtc { get; private set; }

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

    /// <summary>
    /// True when <paramref name="actorId"/> may send another request right now under the account-target
    /// abuse cap. A sender change (the other side is now sending) or an elapsed window always allows a
    /// send — the cap only throttles repeated sends from the same account to the same target in one window.
    /// </summary>
    public bool CanSend(Guid actorId, DateTime nowUtc, int maxPerWindow, TimeSpan window)
    {
        if (LastSenderId != actorId || SendWindowStartUtc is not DateTime start || nowUtc - start >= window)
            return true;
        return SendCountInWindow < maxPerWindow;
    }

    /// <summary>Retry-after instant to report when <see cref="CanSend"/> currently returns false.</summary>
    public DateTime SendRetryAfterUtc(TimeSpan window) => (SendWindowStartUtc ?? DateTime.UtcNow) + window;

    /// <summary>Records a send for the abuse cap. Call only immediately before persisting an allowed send.</summary>
    public void RecordSend(Guid actorId, DateTime nowUtc, TimeSpan window)
    {
        if (LastSenderId != actorId || SendWindowStartUtc is not DateTime start || nowUtc - start >= window)
        {
            LastSenderId = actorId;
            SendWindowStartUtc = nowUtc;
            SendCountInWindow = 1;
        }
        else
        {
            SendCountInWindow += 1;
        }
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
    public SearchVisibility SearchVisibility { get; private set; } = SearchVisibility.Everyone;
    public FriendsListVisibility FriendsListVisibility { get; private set; } = FriendsListVisibility.Friends;

    /// <summary>
    /// Monotonic counter bumped on any privacy-relevant settings change (any of the three settings above).
    /// Used to invalidate/bind viewer-specific cursors and caches; not itself one of the independent settings.
    /// </summary>
    public long PrivacyPolicyVersion { get; private set; } = 1;

    private UserFriendSettings() { }

    public static UserFriendSettings CreateDefault(Guid userId) => new()
    {
        UserId = userId,
        FriendRequestPrivacy = FriendRequestPrivacy.Anyone,
        SearchVisibility = SearchVisibility.Everyone,
        FriendsListVisibility = FriendsListVisibility.Friends,
        PrivacyPolicyVersion = 1,
    };

    /// <summary>
    /// Independently updates any subset of the three settings (null = leave unchanged). Bumps
    /// <see cref="PrivacyPolicyVersion"/> once if anything actually changed.
    /// </summary>
    public void UpdateSettings(
        FriendRequestPrivacy privacy, SearchVisibility? searchVisibility, FriendsListVisibility? friendsListVisibility)
    {
        var changed = false;
        if (privacy != FriendRequestPrivacy) { FriendRequestPrivacy = privacy; changed = true; }
        if (searchVisibility.HasValue && searchVisibility.Value != SearchVisibility) { SearchVisibility = searchVisibility.Value; changed = true; }
        if (friendsListVisibility.HasValue && friendsListVisibility.Value != FriendsListVisibility) { FriendsListVisibility = friendsListVisibility.Value; changed = true; }
        if (changed) { PrivacyPolicyVersion += 1; Touch(); }
    }
}

public enum FriendshipStatus { Pending, Accepted, Declined, Cancelled }

public enum FriendRequestPrivacy { Anyone, FriendsOfFriends, Off }

/// <summary>Independent setting: who can discover this account via bounded username search.</summary>
public enum SearchVisibility { Everyone, FriendsOfFriends, Nobody }

/// <summary>Independent setting: who can view this account's friends list / visible friend count.</summary>
public enum FriendsListVisibility { Everyone, Friends, OnlyMe }

/// <summary>Why an accepted/pending edge was force-ended (distinct from Declined/Cancelled states).</summary>
public enum RelationshipEndReason { Removed, Blocked }
