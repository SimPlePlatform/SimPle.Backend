using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Application.Realtime.Presence;

namespace SimPle.UnitTests.Realtime;

/// <summary>
/// Fake-clock boundary tests for <see cref="PresenceRegistry"/> (docs/specs/module-07-realtime-presence-chat-
/// spec.md, "Domain Invariants: presence precedence"). Mirrors the FakeTimeProvider convention already used in
/// tests/SimPle.UnitTests/Matchmaking/ExpirySweeperTests.cs.
/// </summary>
public sealed class PresenceRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(T0);
    private readonly PresenceRegistry _registry;

    public PresenceRegistryTests()
    {
        _registry = new PresenceRegistry(_clock);
    }

    [Fact]
    public void Connect_FirstConnection_ReportsOnlineAndBumpsVersion()
    {
        var userId = Guid.NewGuid();

        var connected = _registry.TryConnect(userId, "conn-1");
        var status = _registry.GetStatus(userId);

        connected.Should().BeTrue();
        status.Status.Should().Be(PresenceStatus.Online);
        status.UserVersion.Should().Be(1);
    }

    [Fact]
    public void Away_At4Minutes59Seconds_StillOnline()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");

        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var status = _registry.GetStatus(userId);

        status.Status.Should().Be(PresenceStatus.Online);
        status.Changed.Should().BeFalse();
    }

    [Fact]
    public void Away_At5Minutes1Second_BecomesAway()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var status = _registry.GetStatus(userId);

        status.Status.Should().Be(PresenceStatus.Away);
        status.Changed.Should().BeTrue();
    }

    [Fact]
    public void Offline_At9SecondsAfterAllDisconnected_NotYetOffline()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");
        _registry.Disconnect(userId, "conn-1");

        _clock.Advance(TimeSpan.FromSeconds(9));
        var status = _registry.GetStatus(userId);

        status.Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void Offline_At11SecondsAfterAllDisconnected_BecomesOfflineAndEvictsBucket()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");
        _registry.Disconnect(userId, "conn-1");

        _clock.Advance(TimeSpan.FromSeconds(11));
        var status = _registry.GetStatus(userId);

        status.Status.Should().Be(PresenceStatus.Offline);
        status.Changed.Should().BeTrue();

        // Bucket evicted: a subsequent query for the same (still-disconnected) user starts fresh at version 0
        // rather than continuing to increment a retained entry.
        var again = _registry.GetStatus(userId);
        again.UserVersion.Should().Be(0);
        again.Changed.Should().BeFalse();
    }

    [Fact]
    public void ReportActivity_ThrottledWithinSixtySeconds_RejectedAndMutatesNothing()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1"); // sets last-activity at T0
        _clock.Advance(TimeSpan.FromSeconds(30)); // within the 60s throttle window of that same stamp

        var accepted = _registry.TryReportActivity(userId, "conn-1");
        accepted.Should().BeFalse();

        // Prove the rejected signal truly mutated nothing: the away-timer base is still T0, so at T0+4:59 total
        // (30s already elapsed + 4:29 more) the connection is still within its original 5-minute Online window.
        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(29));
        _registry.GetStatus(userId).Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void ReportActivity_AfterThrottleWindow_AcceptedAndResetsAwayTimer()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");
        _clock.Advance(TimeSpan.FromSeconds(61)); // past the 60s throttle window

        var accepted = _registry.TryReportActivity(userId, "conn-1");
        accepted.Should().BeTrue();

        // Away-timer reset by the accepted activity: 4 more minutes from here is still well within Online.
        _clock.Advance(TimeSpan.FromMinutes(4));
        _registry.GetStatus(userId).Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void MaxAggregation_OneConnectionActive_KeepsUserOnlineDespiteAnotherIdleConnection()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");
        _clock.Advance(TimeSpan.FromMinutes(3));
        _registry.TryConnect(userId, "conn-2");
        _clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1)); // conn-1 now 5:01 idle, conn-2 only 2:01

        _registry.GetStatus(userId).Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void SixthConnection_ForSameUser_Rejected()
    {
        var userId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _registry.TryConnect(userId, $"conn-{i}").Should().BeTrue();

        _registry.TryConnect(userId, "conn-6").Should().BeFalse();
    }

    [Fact]
    public void MemberOfLobby_WhileConnected_DrivesInLobbyStatus()
    {
        var userId = Guid.NewGuid();
        var lobbyId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");

        var result = _registry.SetLobbyMembership(userId, lobbyId, isMember: true);

        result.Status.Should().Be(PresenceStatus.InLobby);
    }

    [Fact]
    public void SubscribedLobby_DoesNotDriveInLobbyStatus()
    {
        // SubscribedLobbyIds (fan-out) is a hub/group concern entirely separate from MemberOfLobbyIds (presence).
        // Presence never learns about a subscription that isn't also reported as membership.
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");

        var status = _registry.GetStatus(userId);

        status.Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void ServerEpoch_IsStableAcrossCalls()
    {
        var userId = Guid.NewGuid();
        _registry.TryConnect(userId, "conn-1");

        var first = _registry.GetStatus(userId).ServerEpoch;
        var second = _registry.GetStatus(userId).ServerEpoch;

        first.Should().Be(second);
        first.Should().Be(_registry.ServerEpoch);
    }
}
