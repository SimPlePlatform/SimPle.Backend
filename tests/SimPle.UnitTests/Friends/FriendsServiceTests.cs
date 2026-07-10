using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Friends;
using SimPle.Application.Friends.DTOs;
using SimPle.Application.Friends.Services;
using SimPle.Domain.Friends;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.UnitTests.Friends;

/// <summary>
/// Re-authored in Module 3 backend Sub-session C against the reconciled contract (see
/// docs/specs/module-03-friends-social-graph-spec.md and the hardened brief). These are pure service-level
/// unit tests over mocked <see cref="IFriendRepository"/>/<see cref="IUserRepository"/>: they prove the
/// state machine, outcome discriminators (R1/R2), BOLA-safe 404s (R8), cooldown gating, discovery visibility
/// matrix, the canonical error catalogue (R12), and outbox payload redaction. Invariants that need a real
/// database engine — the unordered-pair expression index, xmin concurrency, the 23505 cross-send converge,
/// CHECK/FK/cascade — are asserted in the disposable-Postgres suite, never here (brief Risk #1).
/// </summary>
public sealed class FriendsServiceTests
{
    private readonly IFriendRepository _friends = Substitute.For<IFriendRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly FriendsService _service;

    public FriendsServiceTests()
    {
        var storageOptions = Options.Create(new StorageOptions { ReadUrlExpiryMinutes = 15 });
        _storage.CreatePresignedReadUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"https://cdn.test/{ci.ArgAt<string>(0)}");
        _friends.UpsertSettingsAsync(
                Arg.Any<Guid>(), Arg.Any<FriendRequestPrivacy>(), Arg.Any<SearchVisibility?>(),
                Arg.Any<FriendsListVisibility?>(), Arg.Any<CancellationToken>())
            .Returns(ci => SettingsWith(ci.Arg<Guid>(), ci.Arg<FriendRequestPrivacy>()));
        _service = new FriendsService(_friends, _users, _storage, storageOptions, NullLogger<FriendsService>.Instance);
    }

    // ── Test data helpers ─────────────────────────────────────────────────────

    private static User MakeUser(string username = "alice")
    {
        var u = User.Create(username, $"{username}@test.com", "hash", char.ToUpper(username[0]) + username[1..]);
        return u;
    }

    private static User MakePrivateUser(string username = "priv")
    {
        var u = MakeUser(username);
        u.UpdateProfile(u.DisplayName, u.Bio, visibility: ProfileVisibility.Private);
        return u;
    }

    private static Friendship Pending(Guid requesterId, Guid addresseeId) =>
        Friendship.Request(requesterId, addresseeId);

    private static Friendship Accepted(Guid requesterId, Guid addresseeId)
    {
        var f = Friendship.Request(requesterId, addresseeId);
        f.Accept(addresseeId);
        return f;
    }

    private UserFriendSettings SettingsWith(Guid userId, FriendRequestPrivacy privacy)
    {
        var s = UserFriendSettings.CreateDefault(userId);
        s.UpdateSettings(privacy, null, null);
        return s;
    }

    // ── Send: guards & privacy matrix ──────────────────────────────────────────

    [Fact]
    public async Task Send_ToSelf_SelfRequest400()
    {
        var id = Guid.NewGuid();
        var r = await _service.SendFriendRequestAsync(id, id);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.SelfRequest");
    }

    [Fact]
    public async Task Send_TargetMissing_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        _users.GetByIdAsync(target).Returns((User?)null);

        var r = await _service.SendFriendRequestAsync(actor, target);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Send_TargetSuspended_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        target.Suspend();
        _users.GetByIdAsync(target.Id).Returns(target);

        var r = await _service.SendFriendRequestAsync(actor, target.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Send_BlockedEitherDirection_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(true);

        var r = await _service.SendFriendRequestAsync(actor, target.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Send_PrivateNonFriend_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakePrivateUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);
        _friends.AreFriendsAsync(actor, target.Id).Returns(false);

        var r = await _service.SendFriendRequestAsync(actor, target.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Send_PrivacyOff_RequestsDisabled400()
    {
        var actor = MakeUser("alice");
        var target = MakeUser("bob");
        _users.GetByIdAsync(actor.Id).Returns(actor);
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor.Id, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns(SettingsWith(target.Id, FriendRequestPrivacy.Off));

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Error!.Code.Should().Be("Friends.RequestsDisabled");
    }

    [Fact]
    public async Task Send_FoF_NoMutual_NotFriendOfFriend400()
    {
        var actor = MakeUser("alice");
        var target = MakeUser("bob");
        _users.GetByIdAsync(actor.Id).Returns(actor);
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor.Id, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns(SettingsWith(target.Id, FriendRequestPrivacy.FriendsOfFriends));
        _friends.GetMutualFriendCountAsync(actor.Id, target.Id).Returns(0);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Error!.Code.Should().Be("Friends.NotFriendOfFriend");
    }

    [Fact]
    public async Task Send_FoF_WithMutual_Creates201Outcome()
    {
        var actor = MakeUser("alice");
        var target = MakeUser("bob");
        _users.GetByIdAsync(actor.Id).Returns(actor);
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor.Id, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns(SettingsWith(target.Id, FriendRequestPrivacy.FriendsOfFriends));
        _friends.GetMutualFriendCountAsync(actor.Id, target.Id).Returns(2);
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns((Friendship?)null);
        _friends.TryAddFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(AddFriendshipOutcome.Added);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
    }

    // ── Send: outcome discriminators (R1/R2) ────────────────────────────────────

    [Fact]
    public async Task Send_NoEdge_RequestCreated()
    {
        var (actor, target) = SetupSendableTarget();
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns((Friendship?)null);
        _friends.TryAddFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(AddFriendshipOutcome.Added);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
        r.Value.Request.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Send_SameDirectionPending_AlreadyPending()
    {
        var (actor, target) = SetupSendableTarget();
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(Pending(actor.Id, target.Id));

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Value!.Outcome.Should().Be(SendFriendRequestResult.AlreadyPending);
        // No new/updated edge is written for an idempotent same-direction retry.
        await _friends.DidNotReceive().TryAddFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    [Fact]
    public async Task Send_ReversePending_CrossRequestAccepted()
    {
        var (actor, target) = SetupSendableTarget();
        var reverse = Pending(target.Id, actor.Id);   // target already requested actor
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(reverse);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Value!.Outcome.Should().Be(SendFriendRequestResult.CrossRequestAccepted);
        r.Value.Request.Status.Should().Be("Accepted");
        // Exactly one FriendRequestAcceptedV1 is staged for the non-Accepted→Accepted transition.
        await _friends.Received(1).TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(),
            Arg.Is<OutboxMessage>(m => m.EventType == "FriendRequestAcceptedV1"));
    }

    [Fact]
    public async Task Send_WhenAccepted_AlreadyFriends409()
    {
        var (actor, target) = SetupSendableTarget();
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(Accepted(actor.Id, target.Id));

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.AlreadyFriends");
    }

    [Fact]
    public async Task Send_TerminalNoCooldown_Reactivates()
    {
        var (actor, target) = SetupSendableTarget();
        var edge = Pending(actor.Id, target.Id);
        edge.Decline(target.Id, DateTime.UtcNow.AddDays(-1));   // cooldown already expired
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(edge);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
        r.Value.Request.Status.Should().Be("Pending");
    }

    // ── Send: cooldown gating (direction-specific) ──────────────────────────────

    [Fact]
    public async Task Send_DeclinedRequesterWithinCooldown_RequestCooldown409WithRetryAfter()
    {
        var (actor, target) = SetupSendableTarget();
        var until = DateTime.UtcNow.AddDays(7);
        var edge = Pending(actor.Id, target.Id);
        edge.Decline(target.Id, until);   // actor is the requester constrained by the decline cooldown
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(edge);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.RequestCooldown");
        r.Error.RetryAfterUtc.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Send_DeclinedAddresseeInitiatesImmediately_Reactivates()
    {
        // The DECLINING addressee (original addressee) may initiate immediately; the cooldown gate only
        // binds the original requester. Here actor was the addressee of the prior declined cycle.
        var (actor, target) = SetupSendableTarget();
        var edge = Pending(target.Id, actor.Id);            // target was requester, actor was addressee
        edge.Decline(actor.Id, DateTime.UtcNow.AddDays(7)); // cooldown set against target (the requester)
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(edge);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
    }

    [Fact]
    public async Task Send_ConcurrencyExhausted_ConcurrencyConflict409()
    {
        var (actor, target) = SetupSendableTarget();
        // Each retry must re-read a FRESH reverse-pending edge from the DB (a shared object would be mutated
        // in place by Accept() and read as Accepted on the next loop). Every attempt accepts → xmin conflict.
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(_ => Pending(target.Id, actor.Id));
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.ConcurrencyConflict);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.ConcurrencyConflict");
    }

    // ── Send: abuse cap (3/day/account-target) ───────────────────────────────────

    [Fact]
    public async Task Send_CapExceededWithinWindow_SendCapExceeded429WithRetryAfter()
    {
        var (actor, target) = SetupSendableTarget();
        var edge = Pending(actor.Id, target.Id);
        var windowStart = DateTime.UtcNow;
        edge.Decline(target.Id, windowStart.AddDays(-1));   // cooldown already expired, doesn't block resend
        // Three sends already recorded by actor in the still-open 24h window (fresh-row send + two reactivate sends).
        edge.RecordSend(actor.Id, windowStart, TimeSpan.FromDays(1));
        edge.RecordSend(actor.Id, windowStart, TimeSpan.FromDays(1));
        edge.RecordSend(actor.Id, windowStart, TimeSpan.FromDays(1));
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(edge);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.SendCapExceeded");
        r.Error.RetryAfterUtc.Should().BeCloseTo(windowStart.AddDays(1), TimeSpan.FromSeconds(2));
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    [Fact]
    public async Task Send_CapWindowElapsed_AllowsResendDespiteThreePriorSends()
    {
        var (actor, target) = SetupSendableTarget();
        var edge = Pending(actor.Id, target.Id);
        var oldWindowStart = DateTime.UtcNow.AddDays(-2);
        edge.Decline(target.Id, oldWindowStart.AddHours(1));   // cooldown expired relative to now
        edge.RecordSend(actor.Id, oldWindowStart, TimeSpan.FromDays(1));
        edge.RecordSend(actor.Id, oldWindowStart, TimeSpan.FromDays(1));
        edge.RecordSend(actor.Id, oldWindowStart, TimeSpan.FromDays(1));
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns(edge);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
    }

    [Fact]
    public async Task Send_NewTargetUnaffectedByAnotherPairsCap()
    {
        // A cap recorded against one target must never throttle a first-ever send to a different target —
        // the cap lives on the per-pair Friendship row, and a brand-new pair has no row yet.
        var (actor, target) = SetupSendableTarget();
        _friends.GetEdgeAsync(actor.Id, target.Id).Returns((Friendship?)null);
        _friends.TryAddFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(AddFriendshipOutcome.Added);

        var r = await _service.SendFriendRequestAsync(actor.Id, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(SendFriendRequestResult.RequestCreated);
    }

    /// <summary>Sets up a mutually-sendable actor/target (Anyone privacy, not blocked, both resolvable).</summary>
    private (User actor, User target) SetupSendableTarget()
    {
        var actor = MakeUser("alice");
        var target = MakeUser("bob");
        _users.GetByIdAsync(actor.Id).Returns(actor);
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor.Id, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns((UserFriendSettings?)null);   // default Anyone
        _friends.GetMutualFriendCountAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(0);
        return (actor, target);
    }

    // ── Accept (BOLA-safe) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_Missing_NotVisible404()
    {
        _friends.GetByIdAsync(Arg.Any<Guid>()).Returns((Friendship?)null);
        var r = await _service.AcceptFriendRequestAsync(Guid.NewGuid(), Guid.NewGuid());
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Accept_NotAddressee_NotVisible404_NeverForbidden()
    {
        var actor = Guid.NewGuid();
        var edge = Pending(Guid.NewGuid(), Guid.NewGuid());   // actor is neither party (guessed id)
        _friends.GetByIdAsync(edge.Id).Returns(edge);

        var r = await _service.AcceptFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Profile.NotVisible");   // 404, never 403 — no graph leak
    }

    [Fact]
    public async Task Accept_CommittedBlock_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Pending(requester, actor);
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.IsBlockedInEitherDirectionAsync(requester, actor).Returns(true);

        var r = await _service.AcceptFriendRequestAsync(actor, edge.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");   // committed block wins accept-vs-block race
    }

    [Fact]
    public async Task Accept_AlreadyAccepted_Idempotent200()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Accepted(requester, actor);
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.IsBlockedInEitherDirectionAsync(requester, actor).Returns(false);
        _users.GetByIdAsync(requester).Returns(MakeUser("bob"));
        _users.GetByIdAsync(actor).Returns(MakeUser("alice"));

        var r = await _service.AcceptFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Status.Should().Be("Accepted");
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    [Fact]
    public async Task Accept_Declined_NotPending400()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Pending(requester, actor);
        edge.Decline(actor, DateTime.UtcNow.AddDays(7));
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.IsBlockedInEitherDirectionAsync(requester, actor).Returns(false);

        var r = await _service.AcceptFriendRequestAsync(actor, edge.Id);

        r.Error!.Code.Should().Be("Friends.NotPending");
    }

    [Fact]
    public async Task Accept_Pending_Succeeds_EmitsAcceptedEvent()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Pending(requester, actor);
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.IsBlockedInEitherDirectionAsync(requester, actor).Returns(false);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);
        _users.GetByIdAsync(requester).Returns(MakeUser("bob"));
        _users.GetByIdAsync(actor).Returns(MakeUser("alice"));

        var r = await _service.AcceptFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Status.Should().Be("Accepted");
        await _friends.Received(1).TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(),
            Arg.Is<OutboxMessage>(m => m.EventType == "FriendRequestAcceptedV1"));
    }

    // ── Decline ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_NotAddressee_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var edge = Pending(Guid.NewGuid(), Guid.NewGuid());
        _friends.GetByIdAsync(edge.Id).Returns(edge);

        var r = await _service.DeclineFriendRequestAsync(actor, edge.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Decline_AlreadyDeclined_Idempotent200()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Pending(requester, actor);
        edge.Decline(actor, DateTime.UtcNow.AddDays(7));
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _users.GetByIdAsync(requester).Returns(MakeUser("bob"));
        _users.GetByIdAsync(actor).Returns(MakeUser("alice"));

        var r = await _service.DeclineFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Status.Should().Be("Declined");
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    [Fact]
    public async Task Decline_Pending_SetsSevenDayCooldownOnRequester()
    {
        var actor = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var edge = Pending(requester, actor);
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);
        _users.GetByIdAsync(requester).Returns(MakeUser("bob"));
        _users.GetByIdAsync(actor).Returns(MakeUser("alice"));

        var r = await _service.DeclineFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        edge.NextRequestAllowedAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
    }

    // ── Cancel ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_NotRequester_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var edge = Pending(Guid.NewGuid(), Guid.NewGuid());
        _friends.GetByIdAsync(edge.Id).Returns(edge);

        var r = await _service.CancelFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Cancel_Missing_NotVisible404()
    {
        _friends.GetByIdAsync(Arg.Any<Guid>()).Returns((Friendship?)null);
        var r = await _service.CancelFriendRequestAsync(Guid.NewGuid(), Guid.NewGuid());
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Cancel_Pending_Succeeds_SetsCancelCooldown()
    {
        var actor = Guid.NewGuid();
        var edge = Pending(actor, Guid.NewGuid());
        _friends.GetByIdAsync(edge.Id).Returns(edge);
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.CancelFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        edge.NextRequestAllowedAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Cancel_RepeatAfterCancel_Idempotent204()
    {
        var actor = Guid.NewGuid();
        var edge = Pending(actor, Guid.NewGuid());
        edge.Cancel(actor, DateTime.UtcNow.AddHours(24));   // already cancelled by an earlier cancel
        _friends.GetByIdAsync(edge.Id).Returns(edge);

        var r = await _service.CancelFriendRequestAsync(actor, edge.Id);

        r.IsSuccess.Should().BeTrue();
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    // ── Remove ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_NoEdge_NotVisible404()
    {
        _friends.GetEdgeAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((Friendship?)null);
        var r = await _service.RemoveFriendAsync(Guid.NewGuid(), Guid.NewGuid());
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Remove_PendingNotFriends_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var friend = Guid.NewGuid();
        _friends.GetEdgeAsync(actor, friend).Returns(Pending(actor, friend));

        var r = await _service.RemoveFriendAsync(actor, friend);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Remove_Accepted_Succeeds_EmitsRemovedEvent()
    {
        var actor = Guid.NewGuid();
        var friend = Guid.NewGuid();
        _friends.GetEdgeAsync(actor, friend).Returns(Accepted(actor, friend));
        _friends.TryUpdateFriendshipAsync(Arg.Any<Friendship>(), Arg.Any<OutboxMessage>())
            .Returns(UpdateFriendshipOutcome.Updated);

        var r = await _service.RemoveFriendAsync(actor, friend);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(),
            Arg.Is<OutboxMessage>(m => m.EventType == "FriendshipRemovedV1"));
    }

    [Fact]
    public async Task Remove_RepeatAfterRemoval_Idempotent204()
    {
        var actor = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var edge = Accepted(actor, friend);
        edge.Remove(actor);   // already terminal, caller was a participant
        _friends.GetEdgeAsync(actor, friend).Returns(edge);

        var r = await _service.RemoveFriendAsync(actor, friend);

        r.IsSuccess.Should().BeTrue();   // idempotent 204
        await _friends.DidNotReceive().TryUpdateFriendshipAsync(
            Arg.Any<Friendship>(), Arg.Any<OutboxMessage>());
    }

    // ── Block / Unblock ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Block_Self_SelfBlock400()
    {
        var id = Guid.NewGuid();
        var r = await _service.BlockUserAsync(id, id);
        r.Error!.Code.Should().Be("Friends.SelfBlock");
    }

    [Fact]
    public async Task Block_TargetMissing_NotVisible404()
    {
        _users.GetByIdAsync(Arg.Any<Guid>()).Returns((User?)null);
        var r = await _service.BlockUserAsync(Guid.NewGuid(), Guid.NewGuid());
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Block_AlreadyBlocked_Idempotent200()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns(Block.Create(actor, target.Id));

        var r = await _service.BlockUserAsync(actor, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(BlockUserResult.AlreadyBlocked);
    }

    [Fact]
    public async Task Block_NewOnAcceptedEdge_EndsFriendship_StagesRemovedThenBlocked()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns((Block?)null);
        _friends.GetEdgeAsync(actor, target.Id).Returns(Accepted(actor, target.Id));
        _friends.BlockAndCancelFriendshipAsync(
                Arg.Any<Block>(), Arg.Any<Friendship?>(),
                Arg.Any<OutboxMessage>(), Arg.Any<OutboxMessage?>())
            .Returns(AddBlockOutcome.Added);

        var r = await _service.BlockUserAsync(actor, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(BlockUserResult.Blocked);
        // Accepted edge is ended (Cancelled/Blocked) and a FriendshipRemovedV1 accompanies the block event.
        await _friends.Received(1).BlockAndCancelFriendshipAsync(
            Arg.Any<Block>(),
            Arg.Is<Friendship?>(f => f != null && f.Status == FriendshipStatus.Cancelled),
            Arg.Is<OutboxMessage>(m => m.EventType == "UserBlockedV1"),
            Arg.Is<OutboxMessage?>(m => m != null && m.EventType == "FriendshipRemovedV1"));
    }

    [Fact]
    public async Task Block_NewOnPendingEdge_EndsEdge_NoRemovedEvent()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns((Block?)null);
        _friends.GetEdgeAsync(actor, target.Id).Returns(Pending(actor, target.Id));
        _friends.BlockAndCancelFriendshipAsync(
                Arg.Any<Block>(), Arg.Any<Friendship?>(),
                Arg.Any<OutboxMessage>(), Arg.Any<OutboxMessage?>())
            .Returns(AddBlockOutcome.Added);

        var r = await _service.BlockUserAsync(actor, target.Id);

        r.IsSuccess.Should().BeTrue();
        // A pending edge ended by a block emits NO FriendshipRemovedV1 (only Accepted removal does).
        await _friends.Received(1).BlockAndCancelFriendshipAsync(
            Arg.Any<Block>(),
            Arg.Is<Friendship?>(f => f != null && f.Status == FriendshipStatus.Cancelled),
            Arg.Any<OutboxMessage>(),
            Arg.Is<OutboxMessage?>(m => m == null));
    }

    [Fact]
    public async Task Block_RacingDuplicate_Conflict_Idempotent200()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns((Block?)null);
        _friends.GetEdgeAsync(actor, target.Id).Returns((Friendship?)null);
        _friends.BlockAndCancelFriendshipAsync(
                Arg.Any<Block>(), Arg.Any<Friendship?>(),
                Arg.Any<OutboxMessage>(), Arg.Any<OutboxMessage?>())
            .Returns(AddBlockOutcome.Conflict);

        var r = await _service.BlockUserAsync(actor, target.Id);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Outcome.Should().Be(BlockUserResult.AlreadyBlocked);
    }

    [Fact]
    public async Task Block_ConcurrencyConflict_409()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns((Block?)null);
        _friends.GetEdgeAsync(actor, target.Id).Returns((Friendship?)null);
        _friends.BlockAndCancelFriendshipAsync(
                Arg.Any<Block>(), Arg.Any<Friendship?>(),
                Arg.Any<OutboxMessage>(), Arg.Any<OutboxMessage?>())
            .Returns(AddBlockOutcome.ConcurrencyConflict);

        var r = await _service.BlockUserAsync(actor, target.Id);

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Friends.ConcurrencyConflict");
    }

    [Fact]
    public async Task Unblock_NotBlocked_Idempotent204_NoWrite()
    {
        _friends.GetBlockAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((Block?)null);

        var r = await _service.UnblockUserAsync(Guid.NewGuid(), Guid.NewGuid());

        r.IsSuccess.Should().BeTrue();   // idempotent — unblocking restores nothing
        await _friends.DidNotReceive().RemoveBlockAsync(
            Arg.Any<Block>(), Arg.Any<OutboxMessage>());
    }

    [Fact]
    public async Task Unblock_Existing_RemovesBlock_EmitsUnblockedEvent()
    {
        var actor = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var block = Block.Create(actor, blocked);
        _friends.GetBlockAsync(actor, blocked).Returns(block);

        var r = await _service.UnblockUserAsync(actor, blocked);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).RemoveBlockAsync(
            block, Arg.Is<OutboxMessage>(m => m.EventType == "UserUnblockedV1"));
    }

    // ── Discovery visibility matrix (body + timing parity: same work every branch) ──

    [Fact]
    public async Task Discover_Nonexistent_NotVisible404()
    {
        _users.GetByNormalizedUsernameAsync("GHOST").Returns((User?)null);

        var r = await _service.DiscoverByUsernameAsync(Guid.NewGuid(), "ghost");

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_PublicAnyone_Returns_MinimalIdentityOnly()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByNormalizedUsernameAsync(target.NormalizedUsername).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns((UserFriendSettings?)null);
        _friends.GetMutualFriendCountAsync(actor, target.Id).Returns(0);

        var r = await _service.DiscoverByUsernameAsync(actor, target.Username);

        r.IsSuccess.Should().BeTrue();
        r.Value!.UserId.Should().Be(target.Id);
        r.Value.Username.Should().Be(target.Username);
    }

    [Fact]
    public async Task Discover_Private_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakePrivateUser("bob");
        _users.GetByNormalizedUsernameAsync(target.NormalizedUsername).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns((UserFriendSettings?)null);

        var r = await _service.DiscoverByUsernameAsync(actor, target.Username);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_Blocked_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByNormalizedUsernameAsync(target.NormalizedUsername).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(true);
        _friends.GetSettingsAsync(target.Id).Returns((UserFriendSettings?)null);

        var r = await _service.DiscoverByUsernameAsync(actor, target.Username);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_PrivacyOff_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByNormalizedUsernameAsync(target.NormalizedUsername).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns(SettingsWith(target.Id, FriendRequestPrivacy.Off));

        var r = await _service.DiscoverByUsernameAsync(actor, target.Username);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_FoF_NoMutual_NotVisible404_ButWithMutual_Visible()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByNormalizedUsernameAsync(target.NormalizedUsername).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);
        _friends.GetSettingsAsync(target.Id).Returns(SettingsWith(target.Id, FriendRequestPrivacy.FriendsOfFriends));

        _friends.GetMutualFriendCountAsync(actor, target.Id).Returns(0);
        (await _service.DiscoverByUsernameAsync(actor, target.Username))
            .Error!.Code.Should().Be("Profile.NotVisible");

        _friends.GetMutualFriendCountAsync(actor, target.Id).Returns(1);
        (await _service.DiscoverByUsernameAsync(actor, target.Username))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Discover_Self_NotVisible404()
    {
        var actor = MakeUser("alice");
        _users.GetByNormalizedUsernameAsync(actor.NormalizedUsername).Returns(actor);
        _friends.GetSettingsAsync(actor.Id).Returns((UserFriendSettings?)null);

        var r = await _service.DiscoverByUsernameAsync(actor.Id, actor.Username);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_DoesMembershipWorkEvenWhenNonexistent_ForTimingParity()
    {
        // Anti-enumeration: the "never existed" branch must run the same membership/visibility probes as the
        // "resolved but hidden" branch, so latency cannot distinguish them (brief Risk #5).
        var actor = Guid.NewGuid();
        _users.GetByNormalizedUsernameAsync("GHOST").Returns((User?)null);

        await _service.DiscoverByUsernameAsync(actor, "ghost");

        await _friends.Received().IsBlockedInEitherDirectionAsync(actor, Arg.Any<Guid>());
        await _friends.Received().GetSettingsAsync(Arg.Any<Guid>());
        await _friends.Received().GetMutualFriendCountAsync(actor, Arg.Any<Guid>());
    }

    // ── Suggestion dismissal ────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_Self_NotVisible404()
    {
        var id = Guid.NewGuid();
        var r = await _service.DismissSuggestionAsync(id, id);
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Dismiss_Missing_NotVisible404()
    {
        _users.GetByIdAsync(Arg.Any<Guid>()).Returns((User?)null);
        var r = await _service.DismissSuggestionAsync(Guid.NewGuid(), Guid.NewGuid());
        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Dismiss_Blocked_NotVisible404()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(true);

        var r = await _service.DismissSuggestionAsync(actor, target.Id);

        r.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task Dismiss_Valid_UpsertsThirtyDayWindow_Idempotent204()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.IsBlockedInEitherDirectionAsync(actor, target.Id).Returns(false);

        var r = await _service.DismissSuggestionAsync(actor, target.Id);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).UpsertDismissalAsync(
            actor, target.Id,
            Arg.Is<DateTime>(d => d > DateTime.UtcNow.AddDays(29) && d < DateTime.UtcNow.AddDays(31)),
            Arg.Any<CancellationToken>());
    }

    // ── Settings ────────────────────────────────────────────────────────────────

    [Fact]
    public void UserFriendSettings_UpdateSettings_NoActualChange_DoesNotBumpVersion()
    {
        var s = UserFriendSettings.CreateDefault(Guid.NewGuid());
        var versionBefore = s.PrivacyPolicyVersion;

        s.UpdateSettings(s.FriendRequestPrivacy, null, null);

        s.PrivacyPolicyVersion.Should().Be(versionBefore);
    }

    [Fact]
    public void UserFriendSettings_UpdateSettings_ActualChange_BumpsVersionOnce()
    {
        var s = UserFriendSettings.CreateDefault(Guid.NewGuid());
        var versionBefore = s.PrivacyPolicyVersion;

        s.UpdateSettings(FriendRequestPrivacy.Off, SearchVisibility.Nobody, FriendsListVisibility.OnlyMe);

        s.PrivacyPolicyVersion.Should().Be(versionBefore + 1);
        s.FriendRequestPrivacy.Should().Be(FriendRequestPrivacy.Off);
        s.SearchVisibility.Should().Be(SearchVisibility.Nobody);
        s.FriendsListVisibility.Should().Be(FriendsListVisibility.OnlyMe);
    }

    [Fact]
    public async Task GetSettings_NoRow_DefaultsAnyone_NoWrite()
    {
        var actor = Guid.NewGuid();
        _friends.GetSettingsAsync(actor).Returns((UserFriendSettings?)null);

        var r = await _service.GetSettingsAsync(actor);

        r.Value!.FriendRequestPrivacy.Should().Be("Anyone");
        await _friends.DidNotReceive().UpsertSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<FriendRequestPrivacy>(), Arg.Any<SearchVisibility?>(),
            Arg.Any<FriendsListVisibility?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_Invalid_ValidationFailed400()
    {
        var r = await _service.UpdateSettingsAsync(Guid.NewGuid(), "Nope", null, null);
        r.Error!.Code.Should().Be("Validation.Failed");
    }

    [Theory]
    [InlineData("Off", FriendRequestPrivacy.Off)]
    [InlineData("FriendsOfFriends", FriendRequestPrivacy.FriendsOfFriends)]
    [InlineData("anyone", FriendRequestPrivacy.Anyone)]
    public async Task UpdateSettings_Valid_Upserts(string input, FriendRequestPrivacy expected)
    {
        var actor = Guid.NewGuid();
        var r = await _service.UpdateSettingsAsync(actor, input, null, null);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).UpsertSettingsAsync(
            actor, expected, Arg.Any<SearchVisibility?>(), Arg.Any<FriendsListVisibility?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSettings_NoRow_DefaultsSearchEveryoneAndFriendsListFriends()
    {
        var actor = Guid.NewGuid();
        _friends.GetSettingsAsync(actor).Returns((UserFriendSettings?)null);

        var r = await _service.GetSettingsAsync(actor);

        r.Value!.SearchVisibility.Should().Be("Everyone");
        r.Value.FriendsListVisibility.Should().Be("Friends");
    }

    [Fact]
    public async Task UpdateSettings_InvalidSearchVisibility_ValidationFailed()
    {
        var r = await _service.UpdateSettingsAsync(Guid.NewGuid(), "Anyone", "NotAValue", null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task UpdateSettings_InvalidFriendsListVisibility_ValidationFailed()
    {
        var r = await _service.UpdateSettingsAsync(Guid.NewGuid(), "Anyone", null, "NotAValue");
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task UpdateSettings_PartialUpdate_LeavesUnspecifiedFieldsNull()
    {
        var actor = Guid.NewGuid();
        var r = await _service.UpdateSettingsAsync(actor, "Anyone", "Nobody", null);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).UpsertSettingsAsync(
            actor, FriendRequestPrivacy.Anyone, SearchVisibility.Nobody,
            Arg.Is<FriendsListVisibility?>(v => v == null), Arg.Any<CancellationToken>());
    }

    // ── Friend-list search validation ───────────────────────────────────────────

    [Fact]
    public async Task GetFriends_QueryTooLong_ValidationFailed400()
    {
        var r = await _service.GetFriendsAsync(Guid.NewGuid(), new string('a', 101), 20, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task GetFriends_LeadingAtStrippedAndUppercased_PassedToRepo()
    {
        var actor = Guid.NewGuid();
        _friends.GetFriendsPageAsync(actor, "ALICE", 20, null, null)
            .Returns([]);

        var r = await _service.GetFriendsAsync(actor, "@alice", 20, null);

        r.IsSuccess.Should().BeTrue();
        await _friends.Received(1).GetFriendsPageAsync(
            actor, "ALICE", 20, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFriends_InvalidCursor_InvalidCursor400()
    {
        var r = await _service.GetFriendsAsync(Guid.NewGuid(), null, 20, "!!!not-a-cursor!!!");
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task GetRequests_InvalidCursor_InvalidCursor400()
    {
        var r = await _service.GetRequestsAsync(Guid.NewGuid(), "incoming", 20, "###");
        r.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task GetBlocks_InvalidCursor_InvalidCursor400()
    {
        var r = await _service.GetBlocksAsync(Guid.NewGuid(), 20, "###");
        r.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    // ── Outbox payload redaction (privacy) ──────────────────────────────────────

    [Fact]
    public async Task Block_OutboxPayload_ContainsOnlyIds_NoProfileOrReason()
    {
        var actor = Guid.NewGuid();
        var target = MakeUser("bob");
        _users.GetByIdAsync(target.Id).Returns(target);
        _friends.GetBlockAsync(actor, target.Id).Returns((Block?)null);
        _friends.GetEdgeAsync(actor, target.Id).Returns((Friendship?)null);

        OutboxMessage? captured = null;
        _friends.BlockAndCancelFriendshipAsync(
                Arg.Any<Block>(), Arg.Any<Friendship?>(),
                Arg.Do<OutboxMessage>(m => captured = m), Arg.Any<OutboxMessage?>())
            .Returns(AddBlockOutcome.Added);

        await _service.BlockUserAsync(actor, target.Id);

        captured.Should().NotBeNull();
        var payload = captured!.Payload;
        payload.Should().Contain(target.Id.ToString());
        // No profile snapshot or block reason leaks into the integration event.
        payload.Should().NotContain(target.Username);
        payload.Should().NotContain(target.Email);
        payload.ToLowerInvariant().Should().NotContain("reason");
        payload.ToLowerInvariant().Should().NotContain("displayname");
    }
}
