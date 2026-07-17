using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Application.Outbox;
using SimPle.Application.Realtime.Contracts;
using SimPle.Application.Realtime.Presence;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Realtime.Outbox;

/// <summary>
/// Consumes every Lobby-aggregate integration event (docs/specs/module-07-realtime-presence-chat-spec.md,
/// "Activation watermark") and turns it into the thin <c>LobbyChanged</c> hint — never lobby state itself. Only
/// events built via <see cref="LobbyOutbox"/>'s Lobby-aggregate helper are consumed: <c>LobbyInvite*</c> and
/// <c>MatchRequestedV1</c> key off a different aggregate id (the invite/request, not the lobby) and a different
/// domain-version counter, so mixing them into this handler's revision bookkeeping would be wrong, not just
/// unnecessary.
///
/// <para>
/// <strong>Backfill suppression.</strong> The first time this handler ever runs, <c>OutboxRepository
/// .BackfillMissingDeliveriesAsync</c> materializes a delivery row for every historical Lobby-aggregate event —
/// without a watermark, that first boot would replay the platform's entire lobby history as live realtime
/// traffic. <see cref="IOutboxActivationStore"/> captures <c>MAX(OccurredAtUtc, Id)</c> over this handler's own
/// event types at first activation; any message at or before that instant is pre-existing history and is
/// suppressed with no fan-out at all.
/// </para>
///
/// <para>
/// <strong>Revision bookkeeping is deliberately in-memory and per-process</strong> (<see
/// cref="_lastNotifiedRevision"/>), the same tradeoff B1's presence registry already makes — <c>OutboxDelivery</c>
/// is the durable at-least-once record; this dictionary only exists to collapse same-revision fan-out and detect
/// gaps within one process's lifetime. Losing it on restart costs at most a redundant hint or two, never a
/// correctness problem, because <c>LobbyChanged</c> is a hint the client always resolves by re-fetching the
/// authoritative snapshot.
/// </para>
/// </summary>
public sealed class LobbyRealtimeHandler : IOutboxHandler
{
    public const string Name = "lobby-realtime";

    private static readonly IReadOnlyList<string> ConsumedEventTypes = new[]
    {
        LobbyOutbox.LobbyCreated,
        LobbyOutbox.LobbyMemberJoined,
        LobbyOutbox.LobbyMemberLeft,
        LobbyOutbox.LobbyMemberKicked,
        LobbyOutbox.LobbyHostTransferred,
        LobbyOutbox.LobbySettingsChanged,
        LobbyOutbox.LobbyClosed,
        LobbyOutbox.LobbyCredentialRotated,
        LobbyOutbox.LobbyReadinessChanged,
    };

    private readonly IOutboxActivationStore _activation;
    private readonly IRealtimeNotifier _notifier;
    private readonly IPresenceRegistry _presence;
    private readonly IPresenceViewerResolver _viewerResolver;
    private readonly ILobbyRepository _lobbies;
    private readonly TimeProvider _clock;
    private readonly ILogger<LobbyRealtimeHandler> _logger;

    /// <summary>lobbyId -&gt; last-notified <c>Lobby.Revision</c> (the event's <c>AggregateDomainVersion</c>).</summary>
    private readonly ConcurrentDictionary<Guid, long> _lastNotifiedRevision = new();

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LobbyRealtimeHandler(
        IOutboxActivationStore activation,
        IRealtimeNotifier notifier,
        IPresenceRegistry presence,
        IPresenceViewerResolver viewerResolver,
        ILobbyRepository lobbies,
        TimeProvider clock,
        ILogger<LobbyRealtimeHandler> logger)
    {
        _activation = activation;
        _notifier = notifier;
        _presence = presence;
        _viewerResolver = viewerResolver;
        _lobbies = lobbies;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Persisted in every delivery row. Renaming it replays the entire lobby-realtime history.</summary>
    public string HandlerName => Name;

    public IReadOnlyList<string> EventTypes => ConsumedEventTypes;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct = default)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var activation = await _activation.GetOrActivateAsync(HandlerName, ConsumedEventTypes, nowUtc, ct);

        if (IsAtOrBeforeWatermark(message, activation))
            return;

        // Independent of the revision-collapse dedup below: that dedup only exists to collapse the LobbyChanged
        // *hint* when several events share one Lobby.Revision (e.g. Leave + HostTransfer + Close), never to
        // suppress a genuine membership-flag mutation. Presence must react to every one of those sibling events.
        await ApplyPresenceSideEffectAsync(message, ct);

        // For every event built via LobbyOutbox's LobbyEvent() helper — every type in ConsumedEventTypes — the
        // aggregate id *is* the lobby id and the domain version *is* Lobby.Revision. The handler never trusts the
        // payload for anything (docs: "the handler never trusts the payload") — this is metadata on the envelope
        // itself, not the JSON body.
        var lobbyId = message.AggregateId;
        var revision = message.AggregateDomainVersion;

        var lastSeen = _lastNotifiedRevision.GetOrAdd(lobbyId, 0L);

        if (revision <= lastSeen)
        {
            // At or behind the last-announced revision: a duplicate at-least-once redelivery of this exact
            // message, a sibling event collapsed at the same Lobby.Revision (Leave legitimately emits
            // MemberLeftV1 + HostTransferredV1 + LobbyClosedV1 at one revision — they must collapse into
            // exactly one LobbyChanged), or a rare out-of-order older event arriving after a newer one already
            // announced. Nothing new to tell clients either way.
            return;
        }

        // A first-ever post-watermark sighting of a lobby (lastSeen == 0, its GetOrAdd default) has no prior
        // baseline to compare against, so it is accepted as the new baseline rather than flagged as a gap — the
        // client's own GET /api/lobbies/{lobbyId} is the source of truth; this hint only tells it to fetch one.
        if (lastSeen != 0 && revision > lastSeen + 1)
        {
            _lastNotifiedRevision[lobbyId] = revision;
            await _notifier.NotifyResyncRequiredAsync(lobbyId, "gap", (int)revision, ct);
            _logger.LogInformation(
                "Realtime: lobby revision gap detected. LobbyId={LobbyId} LastSeen={LastSeen} Revision={Revision}",
                lobbyId, lastSeen, revision);
            return;
        }

        _lastNotifiedRevision[lobbyId] = revision;
        await _notifier.NotifyLobbyChangedAsync(lobbyId, (int)revision, message.EventType, ct);
    }

    /// <summary>The outbox has no global sequence, so <c>(OccurredAtUtc, Id)</c> — the same key the watermark was
    /// captured with — is the only ordering comparison available.</summary>
    private static bool IsAtOrBeforeWatermark(OutboxMessage message, OutboxHandlerActivation activation)
    {
        if (message.OccurredAtUtc < activation.WatermarkOccurredAtUtc) return true;
        if (message.OccurredAtUtc > activation.WatermarkOccurredAtUtc) return false;

        return activation.WatermarkEventId is Guid watermarkId && message.Id.CompareTo(watermarkId) <= 0;
    }

    /// <summary>
    /// Turns a membership-affecting Lobby event into the presence-registry mutation <see cref="IPresenceRegistry"/>
    /// never gets told about on its own — nothing previously called <c>SetLobbyMembership</c> or
    /// <c>NotifyPresenceChangedAsync</c> for any of these transitions, which is why a lobby seat's presence dot
    /// never lit up (the bug this handler exists to fix).
    /// </summary>
    private async Task ApplyPresenceSideEffectAsync(OutboxMessage message, CancellationToken ct)
    {
        switch (message.EventType)
        {
            case LobbyOutbox.LobbyCreated:
            {
                var payload = JsonSerializer.Deserialize<LobbyCreatedPayload>(message.Payload, PayloadOptions);
                if (payload is null || payload.HostUserId == Guid.Empty) { LogMalformedPayload(message); return; }
                await SetMembershipAndBroadcastAsync(payload.HostUserId, payload.LobbyId, true, ct);
                return;
            }
            case LobbyOutbox.LobbyMemberJoined:
            {
                var payload = JsonSerializer.Deserialize<MemberPayload>(message.Payload, PayloadOptions);
                if (payload is null || payload.UserId == Guid.Empty) { LogMalformedPayload(message); return; }
                await SetMembershipAndBroadcastAsync(payload.UserId, payload.LobbyId, true, ct);
                return;
            }
            case LobbyOutbox.LobbyMemberLeft:
            case LobbyOutbox.LobbyMemberKicked:
            {
                var payload = JsonSerializer.Deserialize<MemberPayload>(message.Payload, PayloadOptions);
                if (payload is null || payload.UserId == Guid.Empty) { LogMalformedPayload(message); return; }
                await SetMembershipAndBroadcastAsync(payload.UserId, payload.LobbyId, false, ct);
                return;
            }
            case LobbyOutbox.LobbyClosed:
            {
                // The event payload carries only lobbyId/reason, so the roster to clear has to come from a
                // re-read. Close/expiry now releases every seat (Lobby.CloseInternal/TryExpire call
                // ReleaseAllJoinedMembers), so JoinedMembers is already empty by the time this handler runs —
                // Members (every seat regardless of state) is the only list that still names who to clear.
                var lobby = await _lobbies.GetByIdAsync(message.AggregateId, ct);
                if (lobby is null) return;

                foreach (var member in lobby.Members)
                    await SetMembershipAndBroadcastAsync(member.UserId, lobby.Id, false, ct);
                return;
            }
        }
    }

    private async Task SetMembershipAndBroadcastAsync(Guid userId, Guid lobbyId, bool isMember, CancellationToken ct)
    {
        var result = _presence.SetLobbyMembership(userId, lobbyId, isMember);
        if (!result.Changed)
            return;

        var viewers = await _viewerResolver.ResolveAsync(userId, ct);
        await _notifier.NotifyPresenceChangedAsync(
            userId, viewers, result.Status.ToString(), result.ServerEpoch, result.UserVersion, ct);
    }

    private void LogMalformedPayload(OutboxMessage message) =>
        _logger.LogWarning(
            "Realtime: presence side-effect payload could not be read. EventType={EventType} EventId={EventId}",
            message.EventType, message.Id);

    /// <summary>Matches <c>LobbyOutbox.LobbyCreatedEvent</c>'s payload.</summary>
    private sealed record LobbyCreatedPayload(Guid LobbyId, Guid HostUserId, string GameSlug);

    /// <summary>Matches <c>LobbyOutbox.MemberJoinedEvent</c>/<c>MemberLeftEvent</c>/<c>MemberKickedEvent</c>'s
    /// shared ids — the kicker's id is irrelevant to presence, so it is not modeled here.</summary>
    private sealed record MemberPayload(Guid LobbyId, Guid UserId);
}
