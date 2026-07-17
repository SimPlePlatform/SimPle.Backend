using System.Text.Json;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Lobbies.Outbox;

/// <summary>
/// Builds Module 6's lobby integration events, mirroring <see cref="SimPle.Application.Friends.Outbox.FriendOutbox"/>
/// and <see cref="SimPle.Application.Games.Outbox.GameOutbox"/>. Every payload carries minimum ids only.
///
/// <strong>No join code, link token, or digest ever appears in a payload.</strong> An outbox row is durable,
/// replayable, and read by every future consumer (M7/M8/M11) — a credential that reached one would be permanently
/// disclosed to modules that have no business holding it (Risk #7).
///
/// Each message is staged inside the same transaction as the aggregate mutation. The unique
/// <c>(AggregateId, EventType, AggregateDomainVersion)</c> index is what makes a retried transition idempotent:
/// <see cref="Lobby.Revision"/> is the domain version, so replaying a command at the same revision cannot stage
/// the same logical event twice.
///
/// <c>MatchRequestedV1</c> is the one event M8 consumes to create a match. Committing it means a durable
/// <em>request</em> exists — never that a match does (Risk #6).
/// </summary>
public static class LobbyOutbox
{
    public const int EventVersion = 1;

    private const string LobbyAggregate = "Lobby";
    private const string InviteAggregate = "LobbyInvite";
    private const string StartRequestAggregate = "LobbyStartRequest";

    public const string LobbyCreated = "LobbyCreatedV1";
    public const string LobbyMemberJoined = "LobbyMemberJoinedV1";
    public const string LobbyMemberLeft = "LobbyMemberLeftV1";
    public const string LobbyMemberKicked = "LobbyMemberKickedV1";
    public const string LobbyHostTransferred = "LobbyHostTransferredV1";
    public const string LobbySettingsChanged = "LobbySettingsChangedV1";
    public const string LobbyClosed = "LobbyClosedV1";
    public const string LobbyInviteCreated = "LobbyInviteCreatedV1";
    public const string LobbyInviteAccepted = "LobbyInviteAcceptedV1";
    public const string LobbyInviteRevoked = "LobbyInviteRevokedV1";
    public const string LobbyCredentialRotated = "LobbyCredentialRotatedV1";
    public const string MatchRequested = "MatchRequestedV1";

    /// <summary>Added for M07-B2: <c>LobbyRealtimeHandler</c>'s one new consumed event type, so a readiness
    /// toggle produces the same <c>LobbyChanged</c> hint every other Lobby-aggregate mutation does. Ids only,
    /// matching every other event on this aggregate.</summary>
    public const string LobbyReadinessChanged = "LobbyReadinessChangedV1";

    public static OutboxMessage LobbyCreatedEvent(Lobby lobby) =>
        LobbyEvent(lobby, LobbyCreated, new { lobbyId = lobby.Id, hostUserId = lobby.HostUserId, gameSlug = lobby.GameSlug });

    public static OutboxMessage MemberJoinedEvent(Lobby lobby, Guid userId) =>
        LobbyEvent(lobby, LobbyMemberJoined, new { lobbyId = lobby.Id, userId });

    public static OutboxMessage MemberLeftEvent(Lobby lobby, Guid userId) =>
        LobbyEvent(lobby, LobbyMemberLeft, new { lobbyId = lobby.Id, userId });

    public static OutboxMessage MemberKickedEvent(Lobby lobby, Guid userId, Guid removedByUserId) =>
        LobbyEvent(lobby, LobbyMemberKicked, new { lobbyId = lobby.Id, userId, removedByUserId });

    public static OutboxMessage HostTransferredEvent(Lobby lobby, Guid newHostUserId) =>
        LobbyEvent(lobby, LobbyHostTransferred, new { lobbyId = lobby.Id, newHostUserId });

    public static OutboxMessage SettingsChangedEvent(Lobby lobby) =>
        LobbyEvent(lobby, LobbySettingsChanged, new { lobbyId = lobby.Id, gameSlug = lobby.GameSlug, capabilityVersion = lobby.CapabilityVersion });

    public static OutboxMessage LobbyClosedEvent(Lobby lobby) =>
        LobbyEvent(lobby, LobbyClosed, new { lobbyId = lobby.Id, reason = lobby.ClosedReason?.ToString() });

    /// <summary>Rotation carries the generation only — never the old or new digest, let alone the plaintext.</summary>
    public static OutboxMessage CredentialRotatedEvent(Lobby lobby, int generation) =>
        LobbyEvent(lobby, LobbyCredentialRotated, new { lobbyId = lobby.Id, generation });

    public static OutboxMessage ReadinessChangedEvent(Lobby lobby, Guid userId, bool isReady) =>
        LobbyEvent(lobby, LobbyReadinessChanged, new { lobbyId = lobby.Id, userId, isReady });

    /// <summary>
    /// The event M8 consumes. Ids only: M8 re-reads the lobby it names rather than trusting a settings snapshot
    /// that could already be stale by the time it is delivered.
    /// </summary>
    public static OutboxMessage MatchRequestedEvent(Lobby lobby, LobbyStartRequest request) =>
        OutboxMessage.Create(
            StartRequestAggregate, request.Id, MatchRequested, EventVersion,
            aggregateDomainVersion: request.LobbyRevision, requestCycleId: request.LobbyRevision,
            Serialize(new
            {
                matchRequestId = request.MatchRequestId,
                lobbyId = request.LobbyId,
                lobbyRevision = request.LobbyRevision,
            }));

    public static OutboxMessage InviteCreatedEvent(LobbyInvite invite) =>
        InviteEvent(invite, LobbyInviteCreated);

    public static OutboxMessage InviteAcceptedEvent(LobbyInvite invite) =>
        InviteEvent(invite, LobbyInviteAccepted);

    public static OutboxMessage InviteRevokedEvent(LobbyInvite invite) =>
        InviteEvent(invite, LobbyInviteRevoked);

    /// <summary>
    /// The lobby's <see cref="Lobby.Revision"/> doubles as the aggregate domain version: it is bumped by exactly
    /// the mutations that emit an event, so the outbox's uniqueness index makes a replayed command a no-op rather
    /// than a duplicate delivery.
    /// </summary>
    private static OutboxMessage LobbyEvent(Lobby lobby, string eventType, object payload) =>
        OutboxMessage.Create(
            LobbyAggregate, lobby.Id, eventType, EventVersion,
            aggregateDomainVersion: lobby.Revision, requestCycleId: lobby.Revision,
            Serialize(payload));

    /// <summary>
    /// An invite has no revision counter — but its lifecycle is <c>Pending -&gt; Accepted|Revoked|Expired</c>,
    /// a single terminal step, so the state ordinal is a sufficient and stable domain version.
    /// </summary>
    private static OutboxMessage InviteEvent(LobbyInvite invite, string eventType) =>
        OutboxMessage.Create(
            InviteAggregate, invite.Id, eventType, EventVersion,
            aggregateDomainVersion: (int)invite.State, requestCycleId: (int)invite.State,
            Serialize(new
            {
                inviteId = invite.Id,
                lobbyId = invite.LobbyId,
                inviterUserId = invite.InviterUserId,
                inviteeUserId = invite.InviteeUserId,
            }));

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload);
}
