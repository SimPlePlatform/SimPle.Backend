using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Profiles.DTOs;
using SimPle.Domain.Friends;
using SimPle.Domain.Profiles;
using SimPle.Application.Profiles.Services;
using SimPle.Application.Profiles.Validators;
using SimPle.Domain.Users;

namespace SimPle.UnitTests.Profiles;

public sealed class ProfileServiceTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IUsernameChangeRequestRepository _usernameRequests = Substitute.For<IUsernameChangeRequestRepository>();
    private readonly IRetiredUsernameRepository _retiredUsernames = Substitute.For<IRetiredUsernameRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly IFriendRepository _friends = Substitute.For<IFriendRepository>();
    private readonly ILogger<ProfileService> _logger = Substitute.For<ILogger<ProfileService>>();
    private readonly ProfileService _service;
    private readonly StorageOptions _storageOptions = new()
    {
        ProfilePrefix = "profile-assets",
        UploadUrlExpiryMinutes = 10,
        ReadUrlExpiryMinutes = 15
    };

    public ProfileServiceTests()
    {
        _profiles.GetLinksByUserIdAsync(Arg.Any<Guid>()).Returns((IReadOnlyList<ProfileExternalLink>)[]);
        _profiles.GetInterestsByUserIdAsync(Arg.Any<Guid>()).Returns((IReadOnlyList<ProfileInterestTag>)[]);
        _usernameRequests.GetPendingByUserIdAsync(Arg.Any<Guid>()).Returns((UsernameChangeRequest?)null);
        _usernameRequests.GetByUserIdAndMonthAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((UsernameChangeRequest?)null);
        _storage.CreatePresignedPutUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(x => $"https://upload.example.test/{x.ArgAt<string>(0)}");
        _storage.CreatePresignedReadUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(x => $"https://read.example.test/{x.ArgAt<string>(0)}");
        _storage.ObjectExistsAsync(Arg.Any<string>()).Returns(true);
        // Default: not blocked, not friends, 0 friend count
        _friends.IsBlockedInEitherDirectionAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(false);
        _friends.AreFriendsAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(false);
        _friends.GetFriendCountAsync(Arg.Any<Guid>()).Returns(0);
        _retiredUsernames.IsRetiredAsync(Arg.Any<string>()).Returns(false);
        _service = new ProfileService(
            _users, _profiles, _usernameRequests, _retiredUsernames, _storage, Options.Create(_storageOptions), _friends, _logger);
    }

    private static User MakeUser(string username = "testuser") =>
        User.Create(username, $"{username}@example.com", "hash", "Test User");

    // ── GetMyProfile ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMyProfile_ExistingUser_ReturnsProfileDto()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.GetMyProfileAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.UserId.Should().Be(user.Id);
        result.Value.Username.Should().Be("testuser");
        result.Value.Visibility.Should().Be("Public");
        result.Value.ProfileType.Should().Be("Player");
    }

    [Fact]
    public async Task GetMyProfile_UserNotFound_Fails()
    {
        _users.GetByIdAsync(Arg.Any<Guid>()).Returns((User?)null);

        var result = await _service.GetMyProfileAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("General.NotFound");
    }

    // ── GetPublicProfile ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPublicProfile_PublicUser_VisibleToAnyone()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Username.Should().Be("testuser");
    }

    [Fact]
    public async Task GetPublicProfile_PrivateUser_HiddenFromOthers()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, visibility: ProfileVisibility.Private);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetPublicProfile_PrivateUser_VisibleToOwner()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, visibility: ProfileVisibility.Private);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", user.Id);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetPublicProfile_FriendsOnly_StrangerDenied()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, visibility: ProfileVisibility.FriendsOnly);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        // Default mock: AreFriendsAsync returns false

        var result = await _service.GetPublicProfileAsync("testuser", Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetPublicProfile_FriendsOnly_Friend_Visible()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, visibility: ProfileVisibility.FriendsOnly);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var requesterId = Guid.NewGuid();
        _friends.AreFriendsAsync(user.Id, requesterId).Returns(true);

        var result = await _service.GetPublicProfileAsync("testuser", requesterId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetPublicProfile_FriendsOnly_NullRequester_Denied()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, visibility: ProfileVisibility.FriendsOnly);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetPublicProfile_Blocked_ReturnsNotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var requesterId = Guid.NewGuid();
        _friends.IsBlockedInEitherDirectionAsync(user.Id, requesterId).Returns(true);

        var result = await _service.GetPublicProfileAsync("testuser", requesterId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetPublicProfile_SuspendedUser_ReturnsNotVisible()
    {
        var user = MakeUser();
        user.Suspend();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task BuildProfileDto_IncludesFriendCount()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);
        _friends.GetFriendCountAsync(user.Id).Returns(7);

        var result = await _service.GetMyProfileAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.FriendCount.Should().Be(7);
        await _friends.Received(1).GetFriendCountAsync(user.Id);
    }

    [Fact]
    public async Task GetPublicProfile_NotFound_Fails()
    {
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>()).Returns((User?)null);

        var result = await _service.GetPublicProfileAsync("ghost", null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    // ── GetViewerContext ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetViewerContext_Self_ReturnsSelfWithEditActions()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetViewerContextAsync("testuser", user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("Self");
        result.Value.AllowedActions.Should().Contain("edit");
    }

    [Fact]
    public async Task GetViewerContext_Stranger_ReturnsNoneWithAddFriendAction()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("None");
        result.Value.AllowedActions.Should().Contain("add_friend");
    }

    [Fact]
    public async Task GetViewerContext_Friends_ReturnsFriendsState()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(true);

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("Friends");
        result.Value.AllowedActions.Should().Contain("remove");
    }

    [Fact]
    public async Task GetViewerContext_OutgoingPending_ViewerIsRequester()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetEdgeAsync(user.Id, viewerId).Returns(Friendship.Request(viewerId, user.Id));

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("OutgoingPending");
        result.Value.AllowedActions.Should().Contain("cancel");
    }

    [Fact]
    public async Task GetViewerContext_IncomingPending_TargetIsRequester()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetEdgeAsync(user.Id, viewerId).Returns(Friendship.Request(user.Id, viewerId));

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("IncomingPending");
        result.Value.AllowedActions.Should().Contain("accept");
    }

    [Fact]
    public async Task GetViewerContext_ViewerBlockedTarget_ReturnsBlockedBySelf_BypassingPrivateVisibility()
    {
        var user = MakeUser();
        user.UpdateProfile(user.DisplayName, user.Bio, visibility: ProfileVisibility.Private);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetBlockAsync(viewerId, user.Id).Returns(Block.Create(viewerId, user.Id));

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RelationshipState.Should().Be("BlockedBySelf");
        result.Value.AllowedActions.Should().Contain("unblock");
    }

    [Fact]
    public async Task GetViewerContext_TargetBlockedViewer_ReturnsNotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetBlockAsync(user.Id, viewerId).Returns(Block.Create(user.Id, viewerId));

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetViewerContext_SuspendedTarget_ReturnsNotVisible()
    {
        var user = MakeUser();
        user.Suspend();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetViewerContextAsync("testuser", Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetViewerContext_NotFound_ReturnsNotVisible()
    {
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>()).Returns((User?)null);

        var result = await _service.GetViewerContextAsync("ghost", Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetViewerContext_FriendsListOnlyMe_HidesFriendCount()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.OnlyMe);
        _friends.GetSettingsAsync(user.Id).Returns(settings);

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CanViewFriends.Should().BeFalse();
        result.Value.VisibleFriendCount.Should().BeNull();
    }

    [Fact]
    public async Task GetViewerContext_FriendsListEveryone_ExposesVisibleFriendCount()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        _friends.GetVisibleFriendCountAsync(user.Id, viewerId).Returns(3);

        var result = await _service.GetViewerContextAsync("testuser", viewerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CanViewFriends.Should().BeTrue();
        result.Value.VisibleFriendCount.Should().Be(3);
    }

    // ── GetFriendsList ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFriendsList_AnonymousPublicEveryone_ReturnsPage()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        var friend = MakeUser("carol");
        _friends.GetVisibleFriendsPageAsync(user.Id, Guid.Empty, null, 20, null, null)
            .Returns(new List<User> { friend });

        var result = await _service.GetFriendsListAsync("testuser", null, null, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle(i => i.Username == "carol");
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetFriendsList_AnonymousDefaultFriendsOnlyVisibility_NotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        _friends.GetSettingsAsync(user.Id).Returns((UserFriendSettings?)null);   // default FriendsListVisibility.Friends

        var result = await _service.GetFriendsListAsync("testuser", null, null, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetFriendsList_AnonymousFriendsOnlyBaseProfile_NotVisibleEvenWithEveryoneList()
    {
        var user = MakeUser();
        user.UpdateProfile(user.DisplayName, user.Bio, visibility: ProfileVisibility.FriendsOnly);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);

        var result = await _service.GetFriendsListAsync("testuser", null, null, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetFriendsList_AuthenticatedNonFriendDefaultVisibility_NotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetSettingsAsync(user.Id).Returns((UserFriendSettings?)null);   // default Friends
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(false);

        var result = await _service.GetFriendsListAsync("testuser", viewerId, null, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetFriendsList_AuthenticatedFriend_ReturnsPageIgnoringPrivateBaseProfile()
    {
        var user = MakeUser();
        user.UpdateProfile(user.DisplayName, user.Bio, visibility: ProfileVisibility.Private);
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(true);   // default FriendsListVisibility.Friends
        var friend = MakeUser("carol");
        _friends.GetVisibleFriendsPageAsync(user.Id, viewerId, null, 20, null, null)
            .Returns(new List<User> { friend });

        var result = await _service.GetFriendsListAsync("testuser", viewerId, null, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetFriendsList_Self_ReturnsPageRegardlessOfOnlyMeSetting()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.OnlyMe);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        _friends.GetVisibleFriendsPageAsync(user.Id, user.Id, null, 20, null, null)
            .Returns(new List<User>());

        var result = await _service.GetFriendsListAsync("testuser", user.Id, null, 20, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetFriendsList_BlockedEitherDirection_NotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        _friends.IsBlockedInEitherDirectionAsync(user.Id, viewerId).Returns(true);

        var result = await _service.GetFriendsListAsync("testuser", viewerId, null, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetFriendsList_QueryTooShort_ValidationFailed()
    {
        var result = await _service.GetFriendsListAsync("testuser", null, "a", 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task GetFriendsList_CursorWrongListContext_InvalidCursor()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        var mutualCursor = Cursor.EncodeProfileList(
            "CAROL", Guid.NewGuid(), user.Id, string.Empty, settings.PrivacyPolicyVersion, "mutual");

        var result = await _service.GetFriendsListAsync("testuser", null, null, 20, mutualCursor);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task GetFriendsList_FullPage_EncodesNextCursor()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        var friend = MakeUser("carol");
        _friends.GetVisibleFriendsPageAsync(user.Id, Guid.Empty, null, 1, null, null)
            .Returns(new List<User> { friend });

        var result = await _service.GetFriendsListAsync("testuser", null, null, 1, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.NextCursor.Should().NotBeNull();
    }

    // ── GetMutualFriendsList ──────────────────────────────────────────────────

    [Fact]
    public async Task GetMutualFriendsList_NonFriendDefaultVisibility_NotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.GetSettingsAsync(user.Id).Returns((UserFriendSettings?)null);   // default Friends
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(false);

        var result = await _service.GetMutualFriendsListAsync("testuser", viewerId, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetMutualFriendsList_Friend_ReturnsPage()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(true);
        var mutual = MakeUser("dave");
        _friends.GetVisibleMutualFriendsPageAsync(viewerId, user.Id, 20, null, null)
            .Returns(new List<User> { mutual });

        var result = await _service.GetMutualFriendsListAsync("testuser", viewerId, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle(i => i.Username == "dave");
    }

    [Fact]
    public async Task GetMutualFriendsList_EveryoneVisibilityNonFriend_ReturnsPage()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        _friends.GetVisibleMutualFriendsPageAsync(viewerId, user.Id, 20, null, null)
            .Returns(new List<User>());

        var result = await _service.GetMutualFriendsListAsync("testuser", viewerId, 20, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetMutualFriendsList_BlockedEitherDirection_NotVisible()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        var settings = UserFriendSettings.CreateDefault(user.Id);
        settings.UpdateSettings(FriendRequestPrivacy.Anyone, null, FriendsListVisibility.Everyone);
        _friends.GetSettingsAsync(user.Id).Returns(settings);
        _friends.IsBlockedInEitherDirectionAsync(user.Id, viewerId).Returns(true);

        var result = await _service.GetMutualFriendsListAsync("testuser", viewerId, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.NotVisible");
    }

    [Fact]
    public async Task GetMutualFriendsList_CursorWrongListContext_InvalidCursor()
    {
        var user = MakeUser();
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);
        var viewerId = Guid.NewGuid();
        _friends.AreFriendsAsync(user.Id, viewerId).Returns(true);
        var friendsCursor = Cursor.EncodeProfileList(
            "DAVE", Guid.NewGuid(), user.Id, string.Empty, 1, "friends");

        var result = await _service.GetMutualFriendsListAsync("testuser", viewerId, 20, friendsCursor);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    // ── UpdateProfile ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateProfile_ValidRequest_UpdatesAndReturnsDto()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateProfileAsync(user.Id, new UpdateProfileRequestDto(
            DisplayName: "Updated Name",
            Bio: "My bio",
            Region: "NA-East",
            StatusMessage: "Playing!",
            Visibility: "FriendsOnly",
            ProfileType: "Developer"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.DisplayName.Should().Be("Updated Name");
        result.Value.Bio.Should().Be("My bio");
        result.Value.StatusMessage.Should().Be("Playing!");
        result.Value.Visibility.Should().Be("FriendsOnly");
        result.Value.ProfileType.Should().Be("Developer");
        result.Value.Role.Should().Be("Player");
        await _users.Received(1).UpdateAsync(Arg.Any<User>());
    }

    [Fact]
    public async Task UpdateProfile_InvalidVisibility_StillSucceeds_WithNoChange()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        // If the visibility string doesn't parse, the enum stays at its current value.
        var result = await _service.UpdateProfileAsync(user.Id, new UpdateProfileRequestDto(
            "Name", null, null, null, "InvalidEnum", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Visibility.Should().Be("Public"); // unchanged default
    }

    // ── UpdateUsername ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsername_Available_Succeeds()
    {
        var user = MakeUser();
        _users.ExistsByUsernameAsync("NEWHANDLE").Returns(false);
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateUsernameAsync(user.Id, "newhandle");

        result.IsSuccess.Should().BeTrue();
        result.Value!.AppliedImmediately.Should().BeTrue();
        await _users.Received(1).UpdateAsync(Arg.Is<User>(u => u.Username == "newhandle"));
    }

    [Fact]
    public async Task UpdateUsername_Taken_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync(Arg.Any<string>()).Returns(true);

        var result = await _service.UpdateUsernameAsync(user.Id, "takenname");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.UsernameTaken");
    }

    [Fact]
    public async Task UpdateUsername_RetiredName_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync(Arg.Any<string>()).Returns(false);
        _retiredUsernames.IsRetiredAsync("RETIREDNAME").Returns(true);

        var result = await _service.UpdateUsernameAsync(user.Id, "retiredname");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.UsernameTaken");
        await _users.DidNotReceive().UpdateAsync(Arg.Any<User>());
    }

    [Fact]
    public async Task UpdateUsername_Success_RetiresPreviousHandle()
    {
        var user = MakeUser("oldhandle");
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync("NEWHANDLE").Returns(false);

        var result = await _service.UpdateUsernameAsync(user.Id, "newhandle");

        result.IsSuccess.Should().BeTrue();
        await _retiredUsernames.Received(1).AddAsync(Arg.Is<RetiredUsername>(r =>
            r.NormalizedUsername == "OLDHANDLE" && r.PriorOwnerUserId == user.Id));
    }

    [Fact]
    public async Task UpdateUsername_SecondChangeInMonth_CreatesAdminRequest()
    {
        var user = MakeUser();
        var now = DateTime.UtcNow;
        user.RecordImmediateUsernameChange(now.Year, now.Month);
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync("SECONDHANDLE").Returns(false);

        var result = await _service.UpdateUsernameAsync(user.Id, "secondhandle");

        result.IsSuccess.Should().BeTrue();
        result.Value!.AppliedImmediately.Should().BeFalse();
        result.Value.Request!.RequestedUsername.Should().Be("secondhandle");
        result.Value.Request.Status.Should().Be("Pending");
        await _usernameRequests.Received(1).AddAsync(Arg.Is<UsernameChangeRequest>(r =>
            r.UserId == user.Id &&
            r.RequestedUsername == "secondhandle" &&
            r.RequestYear == now.Year &&
            r.RequestMonth == now.Month));
    }

    [Fact]
    public async Task RequestUsernameChange_PendingRequest_UpdatesSameRequest()
    {
        var user = MakeUser();
        var now = DateTime.UtcNow;
        var existing = UsernameChangeRequest.Create(user.Id, "oldhandle", now.Year, now.Month);
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync("NEWHANDLE").Returns(false);
        _usernameRequests.GetPendingByUserIdAsync(user.Id).Returns(existing);

        var result = await _service.RequestUsernameChangeAsync(user.Id, "newhandle");

        result.IsSuccess.Should().BeTrue();
        result.Value!.RequestedUsername.Should().Be("newhandle");
        await _usernameRequests.Received(1).UpdateAsync(Arg.Is<UsernameChangeRequest>(r => r.Id == existing.Id));
        await _usernameRequests.DidNotReceive().AddAsync(Arg.Any<UsernameChangeRequest>());
    }

    [Fact]
    public async Task CancelUsernameChangeRequest_PendingRequest_MarksCancelled()
    {
        var user = MakeUser();
        var now = DateTime.UtcNow;
        var existing = UsernameChangeRequest.Create(user.Id, "oldhandle", now.Year, now.Month);
        _usernameRequests.GetPendingByUserIdAsync(user.Id).Returns(existing);

        var result = await _service.CancelUsernameChangeRequestAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Cancelled");
        result.Value.CanCancel.Should().BeFalse();
        await _usernameRequests.Received(1).UpdateAsync(Arg.Is<UsernameChangeRequest>(r => r.Status == UsernameChangeStatus.Cancelled));
    }

    [Fact]
    public async Task RequestUsernameChange_AdminAllowanceAlreadyUsed_Fails()
    {
        var user = MakeUser();
        var now = DateTime.UtcNow;
        user.RecordAdminUsernameRequest(now.Year, now.Month);
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByUsernameAsync("ANOTHERHANDLE").Returns(false);

        var result = await _service.RequestUsernameChangeAsync(user.Id, "anotherhandle");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Profile.MonthlyAdminRequestUsed");
    }

    // ── UpdateLinks ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLinks_ValidLinks_PersistsAndReturns()
    {
        var userId = Guid.NewGuid();
        var request = new UpdateLinksRequestDto(
        [
            new LinkItemDto("github", "https://github.com/test", null, 0),
            new LinkItemDto("twitter", "https://twitter.com/test", "@test", 1),
        ]);

        var result = await _service.UpdateLinksAsync(userId, request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value![0].Platform.Should().Be("github");
        await _profiles.Received(1).ReplaceLinksAsync(userId, Arg.Is<IReadOnlyList<ProfileExternalLink>>(l => l.Count == 2));
    }

    [Fact]
    public async Task UpdateLinks_EmptyList_ClearsLinks()
    {
        var userId = Guid.NewGuid();
        var result = await _service.UpdateLinksAsync(userId, new UpdateLinksRequestDto([]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
        await _profiles.Received(1).ReplaceLinksAsync(userId, Arg.Is<IReadOnlyList<ProfileExternalLink>>(l => l.Count == 0));
    }

    // ── UpdateInterests ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateInterests_ValidTags_PersistsAndReturns()
    {
        var userId = Guid.NewGuid();
        var request = new UpdateInterestsRequestDto(["board-games", "puzzle-games"]);

        var result = await _service.UpdateInterestsAsync(userId, request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEquivalentTo(["board-games", "puzzle-games"]);
        await _profiles.Received(1).ReplaceInterestsAsync(
            userId, Arg.Is<IReadOnlyList<ProfileInterestTag>>(t => t.Count == 2));
    }

    // ── Validators ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]                           // empty display name
    [InlineData("   ")]                        // whitespace display name
    public async Task UpdateProfileValidator_InvalidInput_HasErrors(string displayName)
    {
        var validator = new UpdateProfileRequestValidator();
        var dto = new UpdateProfileRequestDto(displayName, null, null, null, null, null);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateProfileValidator_ValidInput_Passes()
    {
        var validator = new UpdateProfileRequestValidator();
        var dto = new UpdateProfileRequestDto("Test User", "Bio text", "NA-East", null, "Public", "Player");
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateProfileValidator_AvatarUrl_NotAccepted()
    {
        // AvatarUrl and BannerUrl must not be accepted in profile update requests.
        // Media is managed exclusively through the upload/confirm/remove endpoints.
        var dto = typeof(UpdateProfileRequestDto);
        var properties = dto.GetProperties().Select(p => p.Name).ToArray();
        properties.Should().NotContain("AvatarUrl");
        properties.Should().NotContain("BannerUrl");
    }

    [Fact]
    public async Task CreateAvatarUploadUrl_ValidImage_ReturnsUserScopedKey()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateAvatarUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("avatar.png", "image/png", 1024));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ObjectKey.Should().StartWith($"profile-assets/users/{user.Id}/avatar/");
        result.Value.ObjectKey.Should().EndWith(".png");
    }

    [Fact]
    public async Task CreateBannerUploadUrl_ValidImage_ReturnsUserScopedKey()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateBannerUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("banner.webp", "image/webp", 1024));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ObjectKey.Should().StartWith($"profile-assets/users/{user.Id}/banner/");
        result.Value.ObjectKey.Should().EndWith(".webp");
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    public async Task CreateAvatarUploadUrl_InvalidContentType_Fails(string contentType)
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateAvatarUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("avatar.svg", contentType, 1024));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task CreateAvatarUploadUrl_OversizedAvatar_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateAvatarUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("avatar.png", "image/png", 5 * 1024 * 1024 + 1));

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBannerUploadUrl_OversizedBanner_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateBannerUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("banner.png", "image/png", 10 * 1024 * 1024 + 1));

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAvatarUpload_UserScopedObjectKey_UpdatesProfile()
    {
        var user = MakeUser();
        var key = $"profile-assets/users/{user.Id}/avatar/file.png";
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.ConfirmAvatarUploadAsync(user.Id, key);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasUploadedAvatar.Should().BeTrue();
        result.Value.AvatarUrl.Should().Contain(key);
        await _users.Received(1).UpdateAsync(Arg.Is<User>(u => u.AvatarObjectKey == key));
    }

    [Fact]
    public async Task ConfirmBannerUpload_UserScopedObjectKey_UpdatesProfile()
    {
        var user = MakeUser();
        var key = $"profile-assets/users/{user.Id}/banner/file.png";
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.ConfirmBannerUploadAsync(user.Id, key);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasUploadedBanner.Should().BeTrue();
        result.Value.BannerUrl.Should().Contain(key);
        await _users.Received(1).UpdateAsync(Arg.Is<User>(u => u.BannerObjectKey == key));
    }

    [Fact]
    public async Task ConfirmAvatarUpload_ObjectKeyForDifferentUser_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.ConfirmAvatarUploadAsync(user.Id,
            $"profile-assets/users/{Guid.NewGuid()}/avatar/file.png");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task RemoveAvatar_ClearsObjectKeyAndReturnsFallback()
    {
        var user = MakeUser();
        user.SetAvatarMedia($"profile-assets/users/{user.Id}/avatar/file.png");
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.RemoveAvatarAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasUploadedAvatar.Should().BeFalse();
        result.Value.AvatarUrl.Should().BeNull();
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RemoveBanner_ClearsObjectKeyAndReturnsFallback()
    {
        var user = MakeUser();
        user.SetBannerMedia($"profile-assets/users/{user.Id}/banner/file.png");
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.RemoveBannerAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasUploadedBanner.Should().BeFalse();
        result.Value.BannerUrl.Should().BeNull();
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task UpdateAvatarFallbackColor_ValidColor_UpdatesColor()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateAvatarFallbackColorAsync(user.Id, "#3366AA");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Color.Should().Be("#3366AA");
    }

    [Fact]
    public async Task UpdateBannerFallbackColor_ValidColor_UpdatesColor()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateBannerFallbackColorAsync(user.Id, "#123456");

        result.IsSuccess.Should().BeTrue();
        result.Value!.BannerFallbackColor.Should().Be("#123456");
    }

    [Theory]
    [InlineData("red")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("#12")]
    public async Task UpdateBannerFallbackColor_InvalidColor_Fails(string color)
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateBannerFallbackColorAsync(user.Id, color);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Theory]
    [InlineData("ab")]          // too short
    [InlineData("has space")]   // invalid chars
    [InlineData("a@user")]      // invalid chars
    public async Task UsernameValidator_InvalidUsername_HasErrors(string username)
    {
        var validator = new UpdateUsernameRequestValidator();
        var dto = new UpdateUsernameRequestDto(username);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("validuser")]
    [InlineData("user_123")]
    [InlineData("some.handle-1")]
    public async Task UsernameValidator_ValidUsername_Passes(string username)
    {
        var validator = new UpdateUsernameRequestValidator();
        var dto = new UpdateUsernameRequestDto(username);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task LinksValidator_InvalidPlatform_HasErrors()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto([new LinkItemDto("fakebook", "https://fb.com", null, 0)]);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://github.com/test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("file:///tmp/avatar.png")]
    public async Task LinksValidator_NonHttpsOrDangerousUrl_HasErrors(string url)
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto([new LinkItemDto("github", url, null, 0)]);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task LinksValidator_DuplicatePlatformAndUrl_HasErrors()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto(
        [
            new LinkItemDto("github", "https://github.com/test", null, 0),
            new LinkItemDto("GitHub", "https://github.com/test/", null, 1)
        ]);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task LinksValidator_MaxLinks_Enforced()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto(
        [
            new LinkItemDto("github",    "https://github.com/a",         null, 0),
            new LinkItemDto("xtwitter",  "https://x.com/a",              null, 1),
            new LinkItemDto("instagram", "https://www.instagram.com/a",  null, 2),
            new LinkItemDto("discord",   "https://discord.gg/example",   null, 3),
            new LinkItemDto("github",    "https://github.com/b",         null, 4),
            new LinkItemDto("xtwitter",  "https://x.com/b",              null, 5)
        ]);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("github",    "testuser",                    "https://github.com/testuser")]
    [InlineData("github",    "https://github.com/testuser", "https://github.com/testuser")]
    [InlineData("xtwitter",  "testuser",                    "https://x.com/testuser")]
    [InlineData("xtwitter",  "https://x.com/testuser",      "https://x.com/testuser")]
    [InlineData("xtwitter",  "https://twitter.com/user",    "https://x.com/user")]
    [InlineData("instagram", "testuser",                    "https://www.instagram.com/testuser")]
    [InlineData("instagram", "@testuser",                   "https://www.instagram.com/testuser")]
    public void ProfileExternalLink_NormalizeUrl_ReturnsCanonicalUrl(string platform, string input, string expected)
    {
        var result = ProfileExternalLink.NormalizeUrlForPlatform(platform, input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("github",    "https://evil.com/user")]
    [InlineData("xtwitter",  "https://evil.com/user")]
    [InlineData("instagram", "https://evil.com/user")]
    [InlineData("github",    "javascript:alert(1)")]
    public void ProfileExternalLink_NormalizeUrl_RejectsInvalidDomain(string platform, string input)
    {
        var act = () => ProfileExternalLink.NormalizeUrlForPlatform(platform, input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task LinksValidator_Website_NotAccepted()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto(
        [
            new LinkItemDto("website", "https://example.com", null, 0)
        ]);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateProfileValidator_InvalidProfileType_HasErrors()
    {
        var validator = new UpdateProfileRequestValidator();
        var dto = new UpdateProfileRequestDto("Test User", null, null, null, "Public", "Admin");

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task InterestsValidator_InvalidInterest_HasErrors()
    {
        var validator = new UpdateInterestsRequestValidator();
        var dto = new UpdateInterestsRequestDto(["not-a-real-interest"]);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    // ── Security regression tests ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateProfile_DeveloperType_DoesNotElevateRole()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateProfileAsync(user.Id, new UpdateProfileRequestDto(
            "Dev", null, null, null, "Public", "Developer"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ProfileType.Should().Be("Developer");
        result.Value.Role.Should().Be("Player");
    }

    [Fact]
    public async Task CreateAvatarUploadUrl_SvgContentType_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateAvatarUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("icon.svg", "image/svg+xml", 1024));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task CreateAvatarUploadUrl_GifContentType_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.CreateAvatarUploadUrlAsync(user.Id,
            new ProfileMediaUploadUrlRequestDto("anim.gif", "image/gif", 1024));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task UpdateAvatarFallbackColor_HexInjectionAttempt_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.UpdateAvatarFallbackColorAsync(user.Id, "red; background: url(evil)");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task GetPublicProfile_EuWestRegion_IsNormalizedToEmpty()
    {
        var user = MakeUser();
        // Simulate old data with eu-west region stored in DB.
        user.UpdateProfile("Test", null, "eu-west");
        _users.GetByNormalizedUsernameAsync("TESTUSER").Returns(user);

        var result = await _service.GetPublicProfileAsync("testuser", null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Region.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMyProfile_RegionEuWest_IsNormalizedToEmpty()
    {
        var user = MakeUser();
        user.UpdateProfile("Test", null, "eu-west");
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.GetMyProfileAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Region.Should().BeEmpty();
    }

    [Fact]
    public async Task LinksValidator_JavascriptScheme_Rejected()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto([new LinkItemDto("github", "javascript:alert(1)", null, 0)]);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task LinksValidator_EvilDomain_Rejected()
    {
        var validator = new UpdateLinksRequestValidator();
        var dto = new UpdateLinksRequestDto([new LinkItemDto("github", "https://evil.com/user", null, 0)]);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("github",    "testhandle",                 "https://github.com/testhandle")]
    [InlineData("xtwitter",  "testhandle",                 "https://x.com/testhandle")]
    [InlineData("instagram", "@testhandle",                "https://www.instagram.com/testhandle")]
    [InlineData("xtwitter",  "https://twitter.com/user",   "https://x.com/user")]
    public void ProfileExternalLink_ValidHandleAndUrl_NormalizesCorrectly(
        string platform, string input, string expectedUrl)
    {
        var result = ProfileExternalLink.NormalizeUrlForPlatform(platform, input);
        result.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task UpdateProfileValidator_InvalidVisibility_HasErrors()
    {
        var validator = new UpdateProfileRequestValidator();
        var dto = new UpdateProfileRequestDto("Name", null, null, null, "SuperAdmin", null);

        var result = await validator.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }
}
