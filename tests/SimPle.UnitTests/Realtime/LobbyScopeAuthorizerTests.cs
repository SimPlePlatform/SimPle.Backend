using FluentAssertions;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Realtime.Authorization;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Users;
using SimPle.UnitTests.Lobbies;

namespace SimPle.UnitTests.Realtime;

/// <summary>
/// Authorization matrix for <see cref="LobbyScopeAuthorizer"/> (docs/specs/module-07-realtime-presence-chat-
/// spec.md, "Authorization / Privacy rules"). Every denial must collapse to the exact same
/// <see cref="LobbyErrors.NotFound"/> code the REST API already uses — existence is never disclosed, so a
/// distinct "forbidden" would itself be the leak.
/// </summary>
public sealed class LobbyScopeAuthorizerTests
{
    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFriendRepository _friends = Substitute.For<IFriendRepository>();
    private readonly LobbyScopeAuthorizer _authorizer;

    public LobbyScopeAuthorizerTests()
    {
        _authorizer = new LobbyScopeAuthorizer(_lobbies, _users, _friends);
    }

    private static User ActiveUser() => User.Create("user1", "user1@example.com", "hash", "User One");

    [Fact]
    public async Task Subscribe_Member_Allowed()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        lobby.Join(member, LobbyTestFactory.T0);
        SetUpLobby(lobby);
        SetUpActor(member);

        var result = await _authorizer.AuthorizeAsync(member, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Subscribe_NonMemberOfPublicOpenLobby_Allowed()
    {
        var host = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));
        SetUpLobby(lobby);
        SetUpActor(outsider);

        var result = await _authorizer.AuthorizeAsync(outsider, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Subscribe_NonMemberOfPrivateLobby_DeniedWithPrivacySafeNotFound()
    {
        var host = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        SetUpLobby(lobby);
        SetUpActor(outsider);

        var result = await _authorizer.AuthorizeAsync(outsider, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task Subscribe_UnknownLobby_DeniedWithSamePrivacySafeNotFound()
    {
        var actor = Guid.NewGuid();
        _lobbies.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Lobby?)null);
        SetUpActor(actor);

        var result = await _authorizer.AuthorizeAsync(actor, Guid.NewGuid(), RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task Send_NonMemberOfPublicOpenLobby_Denied()
    {
        // Unlike Subscribe, Send/Delete always require membership regardless of public visibility — an
        // anonymous observer of a public lobby is never allowed to act inside it.
        var host = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));
        SetUpLobby(lobby);
        SetUpActor(outsider);

        var result = await _authorizer.AuthorizeAsync(outsider, lobby.Id, RealtimeAction.Send);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task Delete_Member_Allowed()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        lobby.Join(member, LobbyTestFactory.T0);
        SetUpLobby(lobby);
        SetUpActor(member);

        var result = await _authorizer.AuthorizeAsync(member, lobby.Id, RealtimeAction.Delete);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Member_BlockedByHost_Denied()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        lobby.Join(member, LobbyTestFactory.T0);
        SetUpLobby(lobby);
        SetUpActor(member);
        _friends.IsBlockedInEitherDirectionAsync(member, host, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _authorizer.AuthorizeAsync(member, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task Host_NeverBlockChecksSelf_Allowed()
    {
        var host = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        SetUpLobby(lobby);
        SetUpActor(host);

        var result = await _authorizer.AuthorizeAsync(host, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeTrue();
        await _friends.DidNotReceive().IsBlockedInEitherDirectionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendedActor_Denied()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        lobby.Join(member, LobbyTestFactory.T0);
        SetUpLobby(lobby);

        var suspendedUser = ActiveUser();
        suspendedUser.Suspend(null);
        _users.GetByIdAsync(member, Arg.Any<CancellationToken>()).Returns(suspendedUser);

        var result = await _authorizer.AuthorizeAsync(member, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task DeletedActor_Denied()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var lobby = LobbyTestFactory.Open(host, LobbyTestFactory.T0,
            LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        lobby.Join(member, LobbyTestFactory.T0);
        SetUpLobby(lobby);
        _users.GetByIdAsync(member, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _authorizer.AuthorizeAsync(member, lobby.Id, RealtimeAction.Subscribe);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(LobbyErrors.NotFound);
    }

    private void SetUpLobby(Lobby lobby) =>
        _lobbies.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

    private void SetUpActor(Guid actorUserId) =>
        _users.GetByIdAsync(actorUserId, Arg.Any<CancellationToken>()).Returns(ActiveUser());
}
