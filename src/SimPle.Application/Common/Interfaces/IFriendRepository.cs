using SimPle.Application.Friends;
using SimPle.Domain.Friends;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Data access for the friends/social graph. Cursor reads are keyset-traversed (no offset); write methods
/// stage the caller-built <see cref="OutboxMessage"/> rows inside the same transaction as the aggregate
/// mutation, so a state change and its integration event commit or roll back together.
/// </summary>
public interface IFriendRepository
{
    // ── Reads: single edges / counts ────────────────────────────────────────────

    Task<Friendship?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Friendship?> GetEdgeAsync(Guid userA, Guid userB, CancellationToken ct = default);
    Task<bool> AreFriendsAsync(Guid userA, Guid userB, CancellationToken ct = default);
    Task<int> GetFriendCountAsync(Guid userId, CancellationToken ct = default);
    Task<int> GetIncomingRequestCountAsync(Guid userId, CancellationToken ct = default);
    Task<int> GetOutgoingRequestCountAsync(Guid userId, CancellationToken ct = default);
    Task<int> GetMutualFriendCountAsync(Guid userA, Guid userB, CancellationToken ct = default);

    /// <summary>
    /// Count of <paramref name="targetUserId"/>'s accepted friends, excluding any candidate blocked (either
    /// direction) with <paramref name="viewerId"/> or currently suspended — a lightweight, reusable filtered
    /// count. Full per-candidate profile-visibility filtering for the actual drill-down list lands in 2B.
    /// </summary>
    Task<int> GetVisibleFriendCountAsync(Guid targetUserId, Guid viewerId, CancellationToken ct = default);

    // ── Reads: keyset cursor pages (fetch up to `limit` rows; caller derives nextCursor) ─────────

    /// <summary>Friends ordered by (normalized display name, userId). Optional normalized search query.</summary>
    Task<IReadOnlyList<(Friendship f, User other)>> GetFriendsPageAsync(
        Guid userId, string? normalizedQuery, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="targetUserId"/>'s accepted friends ordered by (normalized display name, userId),
    /// with every candidate's current visibility/block/sanction policy applied against
    /// <paramref name="viewerId"/> before ordering/paging: candidates blocked (either direction) with the
    /// viewer, currently suspended, or <see cref="ProfileVisibility.Private"/> never appear, and never affect
    /// the page length or cursor. Optional normalized search query narrows by username/display-name substring.
    /// </summary>
    Task<IReadOnlyList<User>> GetVisibleFriendsPageAsync(
        Guid targetUserId, Guid viewerId, string? normalizedQuery, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default);

    /// <summary>
    /// Accepted friends common to both <paramref name="viewerId"/> and <paramref name="targetUserId"/>,
    /// ordered by (normalized display name, userId), with the same viewer-block/suspension/Private filtering
    /// as <see cref="GetVisibleFriendsPageAsync"/> applied before ordering/paging.
    /// </summary>
    Task<IReadOnlyList<User>> GetVisibleMutualFriendsPageAsync(
        Guid viewerId, Guid targetUserId, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default);

    /// <summary>
    /// Bounded people-search candidates ranked by (bucket, normalized username, userId) where bucket 0 =
    /// exact username match, 1 = username prefix, 2 = display-name prefix. Excludes <paramref name="viewerId"/>
    /// itself, suspended accounts, candidates blocked (either direction) with the viewer, and search-ineligible
    /// candidates (<see cref="SearchVisibility.Nobody"/>, or <see cref="SearchVisibility.FriendsOfFriends"/>
    /// with zero viewer-visible mutual friends). <paramref name="normalizedQuery"/> must already be
    /// upper-invariant. Never promises a global/hidden total.
    /// </summary>
    Task<IReadOnlyList<(User user, int bucket, int mutualCount, string relationshipState)>> SearchPeopleAsync(
        Guid viewerId, string normalizedQuery, int limit,
        int? afterBucket, string? afterSortKey, Guid? afterId, CancellationToken ct = default);

    /// <summary>Pending requests ordered by (SentAt DESC, Id DESC). Mutual counts projected server-side.</summary>
    Task<IReadOnlyList<(Friendship f, User requester, User addressee, int mutualCount)>> GetRequestsPageAsync(
        Guid userId, string direction, int limit,
        DateTime? afterSentAt, Guid? afterId, CancellationToken ct = default);

    /// <summary>Blocks ordered by (CreatedAt DESC, Id DESC).</summary>
    Task<IReadOnlyList<(Block b, User blocked)>> GetBlocksPageAsync(
        Guid blockerId, int limit, DateTime? afterCreatedAt, Guid? afterId, CancellationToken ct = default);

    /// <summary>
    /// Suggestions ordered by (mutualFriendCount DESC, normalized display name, userId), capped at `limit`.
    /// Excludes connected, blocked (either direction), Off-privacy, FriendsOfFriends-with-no-mutual, Private,
    /// suspended, and un-expired dismissed candidates. FriendsOnly profiles are included.
    /// </summary>
    Task<IReadOnlyList<(User user, int mutualCount)>> GetSuggestionsAsync(
        Guid userId, int limit, CancellationToken ct = default);

    // ── Friendship writes (stage outbox atomically) ─────────────────────────────

    Task<AddFriendshipOutcome> TryAddFriendshipAsync(
        Friendship f, OutboxMessage created, CancellationToken ct = default);

    Task<UpdateFriendshipOutcome> TryUpdateFriendshipAsync(
        Friendship f, OutboxMessage evt, CancellationToken ct = default);

    // ── Block writes ────────────────────────────────────────────────────────────
    // Atomic single unit of work: block insert + the already-EndByBlock()-ed friendship (if any) + events.
    // removedEvent is staged only when the ended edge was Accepted (M10 count delta); a pending edge ended by
    // a block emits no friendship event. Returns Conflict on duplicate block, ConcurrencyConflict on xmin
    // mismatch of the ended edge.

    Task<AddBlockOutcome> BlockAndCancelFriendshipAsync(
        Block block, Friendship? endedEdge, OutboxMessage blockedEvent, OutboxMessage? removedEvent,
        CancellationToken ct = default);

    Task<Block?> GetBlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default);
    Task<bool> IsBlockedInEitherDirectionAsync(Guid userA, Guid userB, CancellationToken ct = default);
    Task RemoveBlockAsync(Block b, OutboxMessage unblockedEvent, CancellationToken ct = default);

    // ── Suggestion dismissals ───────────────────────────────────────────────────

    Task<DismissedFriendSuggestion?> GetDismissalAsync(
        Guid userId, Guid suggestedUserId, CancellationToken ct = default);

    /// <summary>Insert-or-renew a dismissal (idempotent). Race-safe under a concurrent first insert.</summary>
    Task UpsertDismissalAsync(
        Guid userId, Guid suggestedUserId, DateTime expiresAt, CancellationToken ct = default);

    /// <summary>Delete up to <paramref name="batchSize"/> dismissals expired at/before now. Returns rows removed.</summary>
    Task<int> DeleteExpiredDismissalsAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default);

    // ── Settings (read never creates a row) ─────────────────────────────────────

    Task<UserFriendSettings?> GetSettingsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Independently upserts any subset of settings (null = leave unchanged) and returns the result.</summary>
    Task<UserFriendSettings> UpsertSettingsAsync(
        Guid userId, FriendRequestPrivacy privacy, SearchVisibility? searchVisibility,
        FriendsListVisibility? friendsListVisibility, CancellationToken ct = default);
}
