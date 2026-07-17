using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Application.Outbox;
using SimPle.Application.Realtime.Contracts;
using SimPle.Application.Realtime.Outbox;
using SimPle.Application.Realtime.Presence;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Realtime.Outbox;

/// <summary>
/// <see cref="LobbyRealtimeHandler"/>: activation-watermark backfill suppression, duplicate-delivery no-op,
/// same-revision sibling-event collapse, revision-gap detection, and the presence side effects (docs/specs/
/// module-07-realtime-presence-chat-spec.md, "Activation watermark" / Test Matrix).
/// </summary>
public sealed class LobbyRealtimeHandlerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IOutboxActivationStore _activation = Substitute.For<IOutboxActivationStore>();
    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly IPresenceRegistry _presence = Substitute.For<IPresenceRegistry>();
    private readonly IPresenceViewerResolver _viewerResolver = Substitute.For<IPresenceViewerResolver>();
    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly Guid _lobbyId = Guid.NewGuid();

    private readonly LobbyRealtimeHandler _sut;

    public LobbyRealtimeHandlerTests()
    {
        // No activation row yet at T0 for any of these tests: watermark = T0, tie-break = null (an empty
        // outbox at first activation, per OutboxHandlerActivation's doc comment).
        _activation
            .GetOrActivateAsync(LobbyRealtimeHandler.Name, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OutboxHandlerActivation.Activate(LobbyRealtimeHandler.Name, T0, T0, null));

        _sut = new LobbyRealtimeHandler(
            _activation, _notifier, _presence, _viewerResolver, _lobbies, _clock, NullLogger<LobbyRealtimeHandler>.Instance);
    }

    // ── Watermark suppression ───────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_MessageBeforeWatermark_IsSuppressedWithNoFanOut()
    {
        var watermarkId = Guid.NewGuid();
        _activation
            .GetOrActivateAsync(LobbyRealtimeHandler.Name, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OutboxHandlerActivation.Activate(LobbyRealtimeHandler.Name, T0, T0.AddMinutes(5), watermarkId));

        var message = Message(LobbyOutbox.LobbyMemberJoined, revision: 3, occurredAtUtc: T0.AddMinutes(1));

        await _sut.HandleAsync(message);

        await _notifier.DidNotReceiveWithAnyArgs().NotifyLobbyChangedAsync(default, default, default!);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyResyncRequiredAsync(default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_MessageAtExactWatermarkInstantAndId_IsSuppressed()
    {
        var watermarkId = Guid.NewGuid();
        _activation
            .GetOrActivateAsync(LobbyRealtimeHandler.Name, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OutboxHandlerActivation.Activate(LobbyRealtimeHandler.Name, T0, T0.AddMinutes(5), watermarkId));

        // Same instant as the watermark and an Id that sorts at-or-before it (itself) must also suppress —
        // "at or before", not strictly before.
        var message = Message(LobbyOutbox.LobbyMemberJoined, revision: 3, occurredAtUtc: T0.AddMinutes(5), id: watermarkId);

        await _sut.HandleAsync(message);

        await _notifier.DidNotReceiveWithAnyArgs().NotifyLobbyChangedAsync(default, default, default!);
    }

    [Fact]
    public async Task HandleAsync_MessageAfterWatermark_FansOut()
    {
        _activation
            .GetOrActivateAsync(LobbyRealtimeHandler.Name, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OutboxHandlerActivation.Activate(LobbyRealtimeHandler.Name, T0, T0, null));

        var message = Message(LobbyOutbox.LobbyMemberJoined, revision: 1, occurredAtUtc: T0.AddMinutes(1));

        await _sut.HandleAsync(message);

        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 1, LobbyOutbox.LobbyMemberJoined, Arg.Any<CancellationToken>());
    }

    // ── Duplicate delivery ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SameMessageDeliveredTwice_SecondDeliveryIsNoOp()
    {
        var message = Message(LobbyOutbox.LobbyMemberJoined, revision: 4, occurredAtUtc: T0.AddMinutes(1));

        await _sut.HandleAsync(message);
        await _sut.HandleAsync(message);

        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 4, LobbyOutbox.LobbyMemberJoined, Arg.Any<CancellationToken>());
    }

    // ── Revision collapse ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ThreeEventsAtSameRevision_CollapseToExactlyOneLobbyChanged()
    {
        // Lobby.Leave legitimately stages MemberLeftV1 + HostTransferredV1 + LobbyClosedV1 at one Revision.
        var memberLeft = Message(LobbyOutbox.LobbyMemberLeft, revision: 7, occurredAtUtc: T0.AddMinutes(1));
        var hostTransferred = Message(LobbyOutbox.LobbyHostTransferred, revision: 7, occurredAtUtc: T0.AddMinutes(1));
        var lobbyClosed = Message(LobbyOutbox.LobbyClosed, revision: 7, occurredAtUtc: T0.AddMinutes(1));

        await _sut.HandleAsync(memberLeft);
        await _sut.HandleAsync(hostTransferred);
        await _sut.HandleAsync(lobbyClosed);

        await _notifier.ReceivedWithAnyArgs(1).NotifyLobbyChangedAsync(default, default, default!);
        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 7, LobbyOutbox.LobbyMemberLeft, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NextRevisionAfterCollapsedBatch_StillFansOut()
    {
        var atRevision7 = Message(LobbyOutbox.LobbyMemberLeft, revision: 7, occurredAtUtc: T0.AddMinutes(1));
        var atRevision8 = Message(LobbyOutbox.LobbySettingsChanged, revision: 8, occurredAtUtc: T0.AddMinutes(2));

        await _sut.HandleAsync(atRevision7);
        await _sut.HandleAsync(atRevision8);

        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 8, LobbyOutbox.LobbySettingsChanged, Arg.Any<CancellationToken>());
    }

    // ── Gap detection ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_RevisionGapAfterBaseline_TriggersResyncRequiredNotLobbyChanged()
    {
        var baseline = Message(LobbyOutbox.LobbyMemberJoined, revision: 1, occurredAtUtc: T0.AddMinutes(1));
        var gapped = Message(LobbyOutbox.LobbyMemberJoined, revision: 3, occurredAtUtc: T0.AddMinutes(2));

        await _sut.HandleAsync(baseline);
        await _sut.HandleAsync(gapped);

        await _notifier.Received(1).NotifyResyncRequiredAsync(_lobbyId, "gap", 3, Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyLobbyChangedAsync(_lobbyId, 3, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FirstSightingOfLobbyAtNonOneRevision_IsNotTreatedAsGap()
    {
        // No prior baseline for this lobby (e.g. it was created before this process's activation watermark and
        // this is simply its next post-watermark mutation) — nothing to gap-detect against.
        var message = Message(LobbyOutbox.LobbySettingsChanged, revision: 9, occurredAtUtc: T0.AddMinutes(1));

        await _sut.HandleAsync(message);

        await _notifier.DidNotReceiveWithAnyArgs().NotifyResyncRequiredAsync(default, default!, default);
        await _notifier.Received(1).NotifyLobbyChangedAsync(_lobbyId, 9, LobbyOutbox.LobbySettingsChanged, Arg.Any<CancellationToken>());
    }

    // ── Presence side effects ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LobbyCreated_SetsHostMembershipAndBroadcastsWhenChanged()
    {
        var hostUserId = Guid.NewGuid();
        var viewers = new[] { hostUserId };
        _presence.SetLobbyMembership(hostUserId, _lobbyId, true)
            .Returns(new PresenceUpdateResult(Changed: true, PresenceStatus.InLobby, Guid.NewGuid(), UserVersion: 1));
        _viewerResolver.ResolveAsync(hostUserId, Arg.Any<CancellationToken>()).Returns(viewers);

        var payload = $$"""{"lobbyId":"{{_lobbyId}}","hostUserId":"{{hostUserId}}","gameSlug":"chess-lite"}""";
        var message = Message(LobbyOutbox.LobbyCreated, revision: 1, occurredAtUtc: T0.AddMinutes(1), payload: payload);

        await _sut.HandleAsync(message);

        _presence.Received(1).SetLobbyMembership(hostUserId, _lobbyId, true);
        await _notifier.Received(1).NotifyPresenceChangedAsync(
            hostUserId, Arg.Is<IReadOnlyCollection<Guid>>(v => v.SequenceEqual(viewers)),
            "InLobby", Arg.Any<Guid>(), 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LobbyCreated_DoesNotBroadcastWhenMembershipDidNotChange()
    {
        var hostUserId = Guid.NewGuid();
        // Already recorded as in this lobby (e.g. a redelivery) — SetLobbyMembership is a no-op.
        _presence.SetLobbyMembership(hostUserId, _lobbyId, true)
            .Returns(new PresenceUpdateResult(Changed: false, PresenceStatus.InLobby, Guid.NewGuid(), UserVersion: 1));

        var payload = $$"""{"lobbyId":"{{_lobbyId}}","hostUserId":"{{hostUserId}}","gameSlug":"chess-lite"}""";
        var message = Message(LobbyOutbox.LobbyCreated, revision: 1, occurredAtUtc: T0.AddMinutes(1), payload: payload);

        await _sut.HandleAsync(message);

        await _notifier.DidNotReceiveWithAnyArgs().NotifyPresenceChangedAsync(
            default, default!, default!, default, default, default);
    }

    /// <summary>
    /// Regression guard for the fix alongside Lobby.CloseInternal/TryExpire now releasing every seat: this handler
    /// must clear presence membership from the full roster (Members), not JoinedMembers — by the time this handler
    /// re-reads the lobby, JoinedMembers is already empty because closure released every seat.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LobbyClosed_ClearsMembershipForEveryMemberOnTheFullRoster()
    {
        var hostId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(hostId, T0);
        lobby.Join(otherId, T0);
        lobby.TryExpire(T0.AddHours(2));   // closure already released every seat, matching production behavior

        _lobbies.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _presence.SetLobbyMembership(Arg.Any<Guid>(), lobby.Id, false)
            .Returns(new PresenceUpdateResult(Changed: true, PresenceStatus.Online, Guid.NewGuid(), UserVersion: 1));
        _viewerResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());

        var message = OutboxMessage.Create(
            "Lobby", lobby.Id, LobbyOutbox.LobbyClosed, LobbyOutbox.EventVersion,
            aggregateDomainVersion: 2, requestCycleId: 2, payload: $$"""{"lobbyId":"{{lobby.Id}}","reason":"Expired"}""");
        typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.OccurredAtUtc))!.SetValue(message, T0.AddMinutes(1));

        await _sut.HandleAsync(message);

        _presence.Received(1).SetLobbyMembership(hostId, lobby.Id, false);
        _presence.Received(1).SetLobbyMembership(otherId, lobby.Id, false);
    }

    // ── EventTypes scoping ──────────────────────────────────────────────────

    [Fact]
    public void EventTypes_DoesNotIncludeInviteOrMatchRequestEvents()
    {
        _sut.EventTypes.Should().NotContain(LobbyOutbox.LobbyInviteCreated);
        _sut.EventTypes.Should().NotContain(LobbyOutbox.LobbyInviteAccepted);
        _sut.EventTypes.Should().NotContain(LobbyOutbox.LobbyInviteRevoked);
        _sut.EventTypes.Should().NotContain(LobbyOutbox.MatchRequested);
        _sut.EventTypes.Should().Contain(LobbyOutbox.LobbyReadinessChanged);
    }

    private OutboxMessage Message(string eventType, long revision, DateTime occurredAtUtc, Guid? id = null, string payload = "{}")
    {
        var message = OutboxMessage.Create(
            "Lobby", _lobbyId, eventType, LobbyOutbox.EventVersion,
            aggregateDomainVersion: revision, requestCycleId: (int)revision, payload: payload);

        typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.OccurredAtUtc))!.SetValue(message, occurredAtUtc);
        if (id is Guid explicitId)
            typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.Id))!.SetValue(message, explicitId);

        return message;
    }
}
