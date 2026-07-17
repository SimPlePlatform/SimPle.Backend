using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Chat;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Realtime;
using SimPle.Application.Realtime.Authorization;
using SimPle.Application.Realtime.Contracts;
using SimPle.Domain.Chat;
using SimPle.Domain.Common;
using SimPle.Domain.Users;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Chat;

/// <summary>
/// Command/query-layer tests for <see cref="ChatService"/>: idempotent send, author-only tombstone delete, and
/// keyset history pagination (docs/specs/module-07-realtime-presence-chat-spec.md).
/// </summary>
public sealed class ChatServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IChatRepository _chat = Substitute.For<IChatRepository>();
    private readonly IRealtimeScopeAuthorizer _authorizer = Substitute.For<IRealtimeScopeAuthorizer>();
    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly IRealtimeRateLimiter _rateLimiter = Substitute.For<IRealtimeRateLimiter>();
    private readonly IChatProfanityFilter _profanity = Substitute.For<IChatProfanityFilter>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly IFriendRepository _friends = Substitute.For<IFriendRepository>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly Guid _lobbyId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly Guid _clientCommandId = Guid.NewGuid();

    private readonly ChatService _sut;

    public ChatServiceTests()
    {
        _authorizer.ScopeKind.Returns(RealtimeEnvelope.LobbyScope);
        _authorizer.AuthorizeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<RealtimeAction>(), Arg.Any<CancellationToken>())
            .Returns(RealtimeScopeAuthorizationResult.Allow());

        _rateLimiter.TryAcquireMessage(Arg.Any<Guid>()).Returns(true);
        _profanity.IsProfane(Arg.Any<string>()).Returns(false);

        _chat.FindByClientCommandIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ChatMessage?)null);
        _chat.AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<ChatMessage>());
        _chat.GetSendersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Roster(call.Arg<IReadOnlyList<Guid>>()));

        // The actor is the lobby's host (and only joined member) by default, so every recipient-filtering call
        // resolves to just the sender unless a test overrides this to add other members/blocks.
        _lobbies.GetByIdAsync(_lobbyId, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Open(_actorId, T0));

        _sut = new ChatService(
            _chat, new[] { _authorizer }, _notifier, _rateLimiter, _profanity, _storage,
            Options.Create(new StorageOptions()), _lobbies, _friends, _clock,
            NullLogger<ChatService>.Instance);
    }

    // ── Send ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_Authorized_PersistsAndNotifies()
    {
        var result = await _sut.SendAsync(_actorId, _lobbyId, "hello world", _clientCommandId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Body.Should().Be("hello world");
        result.Value.Deleted.Should().BeFalse();
        result.Value.LobbyId.Should().Be(_lobbyId);
        result.Value.Sender.UserId.Should().Be(_actorId);

        await _chat.Received(1).AddAsync(
            Arg.Is<ChatMessage>(m => m.SenderId == _actorId && m.ScopeId == _lobbyId && m.Body == "hello world"),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyChatMessageCreatedAsync(
            _lobbyId, Arg.Is<IReadOnlyCollection<Guid>>(r => r.Contains(_actorId)),
            Arg.Is<ChatMessageDto>(dto => dto.Body == "hello world"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_NotAuthorized_ReturnsChatNotFoundAndNeverChecksRateLimitOrProfanity()
    {
        _authorizer.AuthorizeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<RealtimeAction>(), Arg.Any<CancellationToken>())
            .Returns(RealtimeScopeAuthorizationResult.Deny("Lobbies.NotFound"));

        var result = await _sut.SendAsync(_actorId, _lobbyId, "hello", _clientCommandId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.NotFound);
        _rateLimiter.DidNotReceive().TryAcquireMessage(Arg.Any<Guid>());
        _profanity.DidNotReceive().IsProfane(Arg.Any<string>());
        await _chat.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_DuplicateClientCommandId_ReturnsOriginalWithoutRateLimitOrProfanityCheck()
    {
        var original = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "already sent", _clientCommandId, T0);
        _chat.FindByClientCommandIdAsync(_actorId, _clientCommandId, Arg.Any<CancellationToken>())
            .Returns(original);

        var result = await _sut.SendAsync(_actorId, _lobbyId, "a different body this time", _clientCommandId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Body.Should().Be("already sent");
        result.Value.Id.Should().Be(original.Id);

        _rateLimiter.DidNotReceive().TryAcquireMessage(Arg.Any<Guid>());
        _profanity.DidNotReceive().IsProfane(Arg.Any<string>());
        await _chat.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyChatMessageCreatedAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<ChatMessageDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_RateLimited_ReturnsRateLimitExceededWithRetryAfter()
    {
        _rateLimiter.TryAcquireMessage(_actorId).Returns(false);

        var result = await _sut.SendAsync(_actorId, _lobbyId, "hello", _clientCommandId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.RateLimitExceeded);
        result.Error.RetryAfterUtc.Should().Be(T0.AddSeconds(5));
        await _chat.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_InvalidBody_ReturnsInvalidBodyAndNeverChecksProfanity()
    {
        var result = await _sut.SendAsync(_actorId, _lobbyId, "   ", _clientCommandId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
        _profanity.DidNotReceive().IsProfane(Arg.Any<string>());
        await _chat.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_ProfaneBody_ReturnsProfanityRejected()
    {
        _profanity.IsProfane(Arg.Any<string>()).Returns(true);

        var result = await _sut.SendAsync(_actorId, _lobbyId, "bad word", _clientCommandId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.ProfanityRejected);
        await _chat.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_SenderMissingFromLookup_RendersTombstone()
    {
        _chat.GetSendersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());

        var result = await _sut.SendAsync(_actorId, _lobbyId, "hello", _clientCommandId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Sender.UserId.Should().Be(Guid.Empty);
        result.Value.Sender.DisplayName.Should().Be("Chat participant");
        result.Value.Sender.AvatarUrl.Should().BeNull();
    }

    /// <summary>M07-001 regression: a co-member the sender has blocked (or who has blocked the sender) must
    /// never appear in the delivery list, even though they are still a joined lobby member — a plain group
    /// broadcast would otherwise leak the sender's chat activity to them.</summary>
    [Fact]
    public async Task Send_LobbyHasBlockedCoMember_ExcludesBlockedMemberFromRecipients()
    {
        var blockedMemberId = Guid.NewGuid();
        var unrelatedMemberId = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(_actorId, T0);
        lobby.Join(blockedMemberId, T0);
        lobby.Join(unrelatedMemberId, T0);
        _lobbies.GetByIdAsync(_lobbyId, Arg.Any<CancellationToken>()).Returns(lobby);

        _friends.IsBlockedInEitherDirectionAsync(_actorId, blockedMemberId, Arg.Any<CancellationToken>())
            .Returns(true);
        _friends.IsBlockedInEitherDirectionAsync(_actorId, unrelatedMemberId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.SendAsync(_actorId, _lobbyId, "hello world", _clientCommandId);

        result.IsSuccess.Should().BeTrue();
        await _notifier.Received(1).NotifyChatMessageCreatedAsync(
            _lobbyId,
            Arg.Is<IReadOnlyCollection<Guid>>(r =>
                r.Contains(_actorId) && r.Contains(unrelatedMemberId) && !r.Contains(blockedMemberId)),
            Arg.Any<ChatMessageDto>(), Arg.Any<CancellationToken>());
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AuthorDeletesOwnMessage_TombstonesAndNotifies()
    {
        var message = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "goodbye", Guid.NewGuid(), T0);
        _chat.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var result = await _sut.DeleteAsync(_actorId, message.Id);

        result.IsSuccess.Should().BeTrue();
        message.IsDeleted.Should().BeTrue();
        await _chat.Received(1).SaveAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyChatMessageDeletedAsync(
            _lobbyId, Arg.Is<IReadOnlyCollection<Guid>>(r => r.Contains(_actorId)),
            message.Id, message.DeletedAtUtc!.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NonAuthor_ReturnsForbiddenAndNeverSavesOrNotifies()
    {
        var author = Guid.NewGuid();
        var message = ChatMessage.Create(ChatScope.Lobby, _lobbyId, author, "not yours", Guid.NewGuid(), T0);
        _chat.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        var result = await _sut.DeleteAsync(_actorId, message.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.Forbidden);
        message.IsDeleted.Should().BeFalse();
        await _chat.DidNotReceive().SaveAsync(Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyChatMessageDeletedAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_MessageNotFound_ReturnsChatNotFound()
    {
        _chat.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);

        var result = await _sut.DeleteAsync(_actorId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.NotFound);
    }

    [Fact]
    public async Task Delete_NotAuthorizedForScope_ReturnsChatNotFoundEvenForTheAuthor()
    {
        var message = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "hi", Guid.NewGuid(), T0);
        _chat.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _authorizer.AuthorizeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<RealtimeAction>(), Arg.Any<CancellationToken>())
            .Returns(RealtimeScopeAuthorizationResult.Deny("Lobbies.NotFound"));

        var result = await _sut.DeleteAsync(_actorId, message.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.NotFound);
        await _chat.DidNotReceive().SaveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_RetriedDeleteOnAlreadyDeletedMessage_IsIdempotentAndReplaysOriginalTimestamp()
    {
        var message = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "bye", Guid.NewGuid(), T0);
        message.Delete(_actorId, T0.AddMinutes(1));
        var originalDeletedAt = message.DeletedAtUtc!.Value;
        _chat.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        _clock.Advance(TimeSpan.FromHours(1));
        var result = await _sut.DeleteAsync(_actorId, message.Id);

        result.IsSuccess.Should().BeTrue();
        message.DeletedAtUtc.Should().Be(originalDeletedAt);
        await _notifier.Received(1).NotifyChatMessageDeletedAsync(
            _lobbyId, Arg.Is<IReadOnlyCollection<Guid>>(r => r.Contains(_actorId)),
            message.Id, originalDeletedAt, Arg.Any<CancellationToken>());
    }

    // ── History ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_DefaultLimitAppliedWhenNull()
    {
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>())
            .Returns(new List<ChatMessage>());

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, null);

        result.IsSuccess.Should().BeTrue();
        await _chat.Received(1).GetHistoryPageAsync(
            ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task GetHistory_LimitOutOfRange_ReturnsValidationFailedWithoutQueryingRepository(int limit)
    {
        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, limit);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.ValidationFailed);
        await _chat.DidNotReceive().GetHistoryPageAsync(
            Arg.Any<ChatScope>(), Arg.Any<Guid>(), Arg.Any<ChatHistoryDirection>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHistory_NotAuthorized_ReturnsChatNotFound()
    {
        _authorizer.AuthorizeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<RealtimeAction>(), Arg.Any<CancellationToken>())
            .Returns(RealtimeScopeAuthorizationResult.Deny("Lobbies.NotFound"));

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 30);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.NotFound);
    }

    [Fact]
    public async Task GetHistory_InvalidCursor_ReturnsInvalidCursor()
    {
        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, "not-a-cursor", 30);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidCursor);
    }

    [Fact]
    public async Task GetHistory_BeforeDirection_FullPage_NextCursorDerivedFromOldestRow()
    {
        var rows = ThreeMessagesAscending();
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 3, Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 3);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(3);
        result.Value.NextCursor.Should().Be(Cursor.EncodeTimeId(rows[0].CreatedAt, rows[0].Id));
    }

    [Fact]
    public async Task GetHistory_AfterDirection_FullPage_NextCursorDerivedFromNewestRow()
    {
        var rows = ThreeMessagesAscending();
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.After, null, null, 3, Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.After, null, 3);

        result.IsSuccess.Should().BeTrue();
        result.Value!.NextCursor.Should().Be(Cursor.EncodeTimeId(rows[^1].CreatedAt, rows[^1].Id));
    }

    [Fact]
    public async Task GetHistory_PartialPage_NoNextCursor()
    {
        var rows = ThreeMessagesAscending();
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 30);

        result.IsSuccess.Should().BeTrue();
        result.Value!.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetHistory_DeletedRow_ProjectsNullBodyAndDeletedTrue()
    {
        var deleted = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "secret", Guid.NewGuid(), T0);
        deleted.Delete(_actorId, T0.AddMinutes(1));
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>())
            .Returns(new List<ChatMessage> { deleted });

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 30);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Single().Body.Should().BeNull();
        result.Value.Items.Single().Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistory_SenderMissingFromLookup_RendersTombstone()
    {
        var row = ChatMessage.Create(ChatScope.Lobby, _lobbyId, Guid.NewGuid(), "hi", Guid.NewGuid(), T0);
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>())
            .Returns(new List<ChatMessage> { row });
        _chat.GetSendersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 30);

        var sender = result.Value!.Items.Single().Sender;
        sender.UserId.Should().Be(Guid.Empty);
        sender.DisplayName.Should().Be("Chat participant");
    }

    /// <summary>Post-frontend security review regression: <c>GetHistoryAsync</c> had no block filtering at all
    /// (unlike <c>SendAsync</c>'s <c>GetDeliverableRecipientsAsync</c> path fixed for M07-001), so a blocked
    /// co-member's messages remained visible via REST history on every page load/resync. A message from a
    /// blocked sender must be excluded; the actor's own message and an unrelated sender's message stay
    /// visible.</summary>
    [Fact]
    public async Task GetHistory_RowFromBlockedSender_IsExcludedFromResults()
    {
        var blockedSenderId = Guid.NewGuid();
        var unrelatedSenderId = Guid.NewGuid();
        var ownMessage = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "mine", Guid.NewGuid(), T0);
        var blockedMessage = ChatMessage.Create(
            ChatScope.Lobby, _lobbyId, blockedSenderId, "from blocked user", Guid.NewGuid(), T0.AddSeconds(1));
        var unrelatedMessage = ChatMessage.Create(
            ChatScope.Lobby, _lobbyId, unrelatedSenderId, "from unrelated user", Guid.NewGuid(), T0.AddSeconds(2));
        _chat.GetHistoryPageAsync(
                ChatScope.Lobby, _lobbyId, ChatHistoryDirection.Before, null, null, 30, Arg.Any<CancellationToken>())
            .Returns(new List<ChatMessage> { ownMessage, blockedMessage, unrelatedMessage });

        _friends.IsBlockedInEitherDirectionAsync(_actorId, blockedSenderId, Arg.Any<CancellationToken>())
            .Returns(true);
        _friends.IsBlockedInEitherDirectionAsync(_actorId, unrelatedSenderId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetHistoryAsync(_actorId, _lobbyId, ChatHistoryDirection.Before, null, 30);

        result.IsSuccess.Should().BeTrue();
        var senderIds = result.Value!.Items.Select(i => i.Sender.UserId).ToList();
        senderIds.Should().Contain(_actorId);
        senderIds.Should().Contain(unrelatedSenderId);
        senderIds.Should().NotContain(blockedSenderId);
        result.Value.Items.Should().HaveCount(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<ChatMessage> ThreeMessagesAscending()
    {
        var m1 = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "one", Guid.NewGuid(), T0);
        var m2 = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "two", Guid.NewGuid(), T0.AddSeconds(1));
        var m3 = ChatMessage.Create(ChatScope.Lobby, _lobbyId, _actorId, "three", Guid.NewGuid(), T0.AddSeconds(2));
        return new List<ChatMessage> { m1, m2, m3 };
    }

    private static User MakeUser(Guid id)
    {
        var user = User.Create($"user{id:N}"[..12], $"{id:N}@example.com", "hash", "Test Player");
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(user, id);
        return user;
    }

    private static IReadOnlyDictionary<Guid, User> Roster(IReadOnlyList<Guid> ids) =>
        ids.ToDictionary(id => id, MakeUser);
}
