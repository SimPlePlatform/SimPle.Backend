using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Friends.Outbox;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Application.Outbox.Handlers;
using SimPle.Domain.Friends;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The first outbox consumer (<strong>D3</strong>): applying a new Module 3 block to a lobby the two users share.
///
/// The rule is asymmetric on purpose — a host who blocks someone is not evicted from the lobby they own, and a
/// member who blocks the host has no authority to remove them, so all they can do is leave.
/// </summary>
public sealed class LobbyBlockHandlerTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();

    private readonly LobbyBlockHandler _sut;

    public LobbyBlockHandlerTests()
    {
        _lobbies.GetActiveLobbyForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Lobby?)null);

        _sut = new LobbyBlockHandler(
            _lobbies, new PassThroughCommandRunner(), _clock, NullLogger<LobbyBlockHandler>.Instance);
    }

    [Fact]
    public void ItConsumesUserBlockedV1()
    {
        _sut.EventTypes.Should().ContainSingle().Which.Should().Be(FriendOutbox.UserBlocked);

        // The handler name is persisted in every delivery row. Renaming it silently replays the entire block
        // history against the new name, so it is a wire identifier — not a class name to be refactored freely.
        _sut.HandlerName.Should().Be("lobby-block");
    }

    [Fact]
    public async Task AHostWhoBlocksAMemberRemovesThem()
    {
        var lobby = GivenSharedLobby();

        await _sut.HandleAsync(BlockEvent(blocker: _host, blocked: _member));

        lobby.FindJoinedMember(_member).Should().BeNull();
        lobby.FindJoinedMember(_host).Should().NotBeNull("the host keeps the lobby they own");
        lobby.HostUserId.Should().Be(_host);

        await _lobbies.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<OutboxMessage>>(e => e.Any(x => x.EventType == LobbyOutbox.LobbyMemberKicked)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANonHostWhoBlocksTheHostLeavesInstead()
    {
        // The blocker has no authority to remove the host, so the only thing they can do is take themselves out.
        var lobby = GivenSharedLobby(blockerIsHost: false);

        await _sut.HandleAsync(BlockEvent(blocker: _member, blocked: _host));

        lobby.FindJoinedMember(_member).Should().BeNull();
        lobby.FindJoinedMember(_host).Should().NotBeNull();

        await _lobbies.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<OutboxMessage>>(e => e.Any(x => x.EventType == LobbyOutbox.LobbyMemberLeft)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheTwoUsersShareNoLobby_ItDoesNothing()
    {
        // This is what makes the handler idempotent and makes a historical backfill safe: it decides from *current*
        // membership, never from the event's age. A year-old UserBlockedV1 replayed on a fresh deployment finds no
        // shared lobby and is a no-op — which is why no activation watermark is needed.
        await _sut.HandleAsync(BlockEvent(blocker: _host, blocked: _member));

        await _lobbies.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADuplicateDeliveryIsANoOp_BecauseTheyAreAlreadySeparated()
    {
        // Delivery is at-least-once by construction, so this is not a hypothetical.
        var lobby = GivenSharedLobby();
        var message = BlockEvent(blocker: _host, blocked: _member);

        await _sut.HandleAsync(message);
        _lobbies.ClearReceivedCalls();

        await _sut.HandleAsync(message);

        lobby.FindJoinedMember(_member).Should().BeNull();
        await _lobbies.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenOnlyTheBlockerIsInTheLobby_ItDoesNothing()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);   // the blocked user was never here
        _lobbies.GetActiveLobbyForUserAsync(_host, Arg.Any<CancellationToken>()).Returns(lobby);
        _lobbies.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        await _sut.HandleAsync(BlockEvent(blocker: _host, blocked: _member));

        lobby.FindJoinedMember(_host).Should().NotBeNull();
        await _lobbies.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATerminalLobbyIsLeftAlone()
    {
        var lobby = GivenSharedLobby();
        lobby.Close(LobbyClosedReason.HostLeft, T0);

        await _sut.HandleAsync(BlockEvent(blocker: _host, blocked: _member));

        await _lobbies.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ItReadsTheRealFriendOutboxPayload_WhichIsCamelCase()
    {
        // Regression guard. FriendOutbox serializes from an anonymous object, so the JSON keys are camelCase
        // (`blockerId`) while the handler's payload record is PascalCase. System.Text.Json is case-sensitive by
        // default — without PropertyNameCaseInsensitive every id would deserialize to Guid.Empty and the handler
        // would silently treat every block as unreadable, with no exception and no failed write to notice it by.
        //
        // Built from the *real* FriendOutbox.UserBlockedEvent, not a hand-written JSON string, so this test breaks
        // if the producer's payload shape ever drifts from the consumer's.
        var lobby = GivenSharedLobby();
        var realEvent = FriendOutbox.UserBlockedEvent(Block.Create(_host, _member));

        // Sanity: the producer really does emit camelCase, so the guard is guarding something.
        realEvent.Payload.Should().Contain("blockerId");
        JsonSerializer.Deserialize<PascalCasePayload>(realEvent.Payload)!.BlockerId.Should().Be(Guid.Empty);

        await _sut.HandleAsync(realEvent);

        lobby.FindJoinedMember(_member).Should().BeNull("the handler read the camelCase ids correctly");
    }

    [Fact]
    public async Task AnUnreadablePayloadIsNotRetried()
    {
        // Replaying it would produce the same nothing. Returning (rather than throwing) lets the dispatcher mark it
        // processed instead of burning the retry budget and dead-lettering a row no retry could ever fix.
        GivenSharedLobby();
        var malformed = OutboxMessage.Create("Block", Guid.NewGuid(), FriendOutbox.UserBlocked, 1, 1, 1, "{}");

        var act = async () => await _sut.HandleAsync(malformed);

        await act.Should().NotThrowAsync();
        await _lobbies.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A lobby where the host and one member are both seated.</summary>
    private Lobby GivenSharedLobby(bool blockerIsHost = true)
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_member, T0);

        var blocker = blockerIsHost ? _host : _member;
        _lobbies.GetActiveLobbyForUserAsync(blocker, Arg.Any<CancellationToken>()).Returns(lobby);
        _lobbies.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        return lobby;
    }

    private static OutboxMessage BlockEvent(Guid blocker, Guid blocked) =>
        FriendOutbox.UserBlockedEvent(Block.Create(blocker, blocked));

    private sealed record PascalCasePayload(Guid BlockId, Guid BlockerId, Guid BlockedId);
}
