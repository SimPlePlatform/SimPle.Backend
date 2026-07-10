using System.Text.Json;
using SimPle.Domain.Friends;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Friends.Outbox;

/// <summary>
/// Builds the seven Module 3 integration events as immutable <see cref="OutboxMessage"/> rows. Every payload
/// carries minimum ids only — no profile snapshot, no block reason — per the privacy contract. Each message
/// is staged inside the same transaction as the aggregate mutation; the unique
/// (AggregateId, EventType, AggregateDomainVersion) index makes a retried transition idempotent.
/// </summary>
public static class FriendOutbox
{
    public const int EventVersion = 1;
    private const string FriendshipAggregate = "Friendship";
    private const string BlockAggregate = "Block";

    public const string RequestCreated = "FriendRequestCreatedV1";
    public const string RequestAccepted = "FriendRequestAcceptedV1";
    public const string RequestDeclined = "FriendRequestDeclinedV1";
    public const string RequestCancelled = "FriendRequestCancelledV1";
    public const string FriendshipRemoved = "FriendshipRemovedV1";
    public const string UserBlocked = "UserBlockedV1";
    public const string UserUnblocked = "UserUnblockedV1";

    public static OutboxMessage RequestCreatedEvent(Friendship f) => Friendship(f, RequestCreated);
    public static OutboxMessage RequestAcceptedEvent(Friendship f) => Friendship(f, RequestAccepted);
    public static OutboxMessage RequestDeclinedEvent(Friendship f) => Friendship(f, RequestDeclined);
    public static OutboxMessage RequestCancelledEvent(Friendship f) => Friendship(f, RequestCancelled);
    public static OutboxMessage FriendshipRemovedEvent(Friendship f) => Friendship(f, FriendshipRemoved);

    public static OutboxMessage UserBlockedEvent(Block b) => BlockEvent(b, UserBlocked);
    public static OutboxMessage UserUnblockedEvent(Block b) => BlockEvent(b, UserUnblocked);

    private static OutboxMessage Friendship(Friendship f, string eventType) =>
        OutboxMessage.Create(
            FriendshipAggregate, f.Id, eventType, EventVersion,
            f.DomainVersion, f.RequestCycleId,
            Serialize(new
            {
                relationshipId = f.Id,
                requesterId = f.RequesterId,
                addresseeId = f.AddresseeId,
            }));

    private static OutboxMessage BlockEvent(Block b, string eventType) =>
        OutboxMessage.Create(
            BlockAggregate, b.Id, eventType, EventVersion,
            aggregateDomainVersion: 1, requestCycleId: 1,
            Serialize(new
            {
                blockId = b.Id,
                blockerId = b.BlockerId,
                blockedId = b.BlockedId,
            }));

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload);
}
