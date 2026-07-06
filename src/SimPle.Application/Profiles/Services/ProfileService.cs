using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Profiles.DTOs;
using SimPle.Domain.Profiles;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Profiles.Services;

public sealed class ProfileService : IProfileService
{
    private readonly IUserRepository _users;
    private readonly IProfileRepository _profiles;
    private readonly IUsernameChangeRequestRepository _usernameRequests;
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;
    private readonly IFriendRepository _friends;

    private static readonly HashSet<string> AllowedImageTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public ProfileService(
        IUserRepository users,
        IProfileRepository profiles,
        IUsernameChangeRequestRepository usernameRequests,
        IFileStorageService storage,
        IOptions<StorageOptions> storageOptions,
        IFriendRepository friends)
    {
        _users = users;
        _profiles = profiles;
        _usernameRequests = usernameRequests;
        _storage = storage;
        _storageOptions = storageOptions.Value;
        _friends = friends;
    }

    public async Task<Result<ProfileDto>> GetMyProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<ProfileDto>> GetPublicProfileAsync(
        string username, Guid? requesterId, CancellationToken ct = default)
    {
        var normalized = username.Trim().ToUpperInvariant();
        var user = await _users.GetByNormalizedUsernameAsync(normalized, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "Profile not found.");

        if (user.Visibility == ProfileVisibility.Private && user.Id != requesterId)
            return Result<ProfileDto>.Fail("Profile.Private", "This profile is private.");

        // Bidirectional block: either direction → not visible (403, not 404)
        if (requesterId.HasValue
            && await _friends.IsBlockedInEitherDirectionAsync(user.Id, requesterId.Value, ct))
            return Result<ProfileDto>.Fail("Profile.Blocked", "This profile is not available.");

        // FriendsOnly: real accepted-friend check (replaces Module 2 owner-only stub)
        if (user.Visibility == ProfileVisibility.FriendsOnly && user.Id != requesterId)
        {
            if (!requesterId.HasValue || !await _friends.AreFriendsAsync(user.Id, requesterId.Value, ct))
                return Result<ProfileDto>.Fail("Profile.FriendsOnly", "This profile is visible to friends only.");
        }

        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<ProfileDto>> UpdateProfileAsync(
        Guid userId, UpdateProfileRequestDto request, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        ProfileVisibility? visibility = null;
        if (request.Visibility is not null &&
            Enum.TryParse<ProfileVisibility>(request.Visibility, out var parsed))
            visibility = parsed;

        ProfileType? profileType = null;
        if (request.ProfileType is not null &&
            Enum.TryParse<ProfileType>(request.ProfileType, out var parsedType))
            profileType = parsedType;

        user.UpdateProfile(
            request.DisplayName,
            request.Bio,
            request.Region,
            request.StatusMessage,
            visibility,
            profileType);

        await _users.UpdateAsync(user, ct);
        return await BuildDtoAsync(user, ct);
    }

    public Task<Result<ProfileMediaUploadUrlDto>> CreateAvatarUploadUrlAsync(
        Guid userId, ProfileMediaUploadUrlRequestDto request, CancellationToken ct = default) =>
        CreateUploadUrlAsync(userId, request, "avatar", 5 * 1024 * 1024, ct);

    public Task<Result<ProfileMediaUploadUrlDto>> CreateBannerUploadUrlAsync(
        Guid userId, ProfileMediaUploadUrlRequestDto request, CancellationToken ct = default) =>
        CreateUploadUrlAsync(userId, request, "banner", 10 * 1024 * 1024, ct);

    public Task<Result<ProfileDto>> ConfirmAvatarUploadAsync(Guid userId, string objectKey, CancellationToken ct = default) =>
        ConfirmUploadAsync(userId, objectKey, "avatar", ct);

    public Task<Result<ProfileDto>> ConfirmBannerUploadAsync(Guid userId, string objectKey, CancellationToken ct = default) =>
        ConfirmUploadAsync(userId, objectKey, "banner", ct);

    public async Task<Result<ProfileDto>> RemoveAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        var previousKey = user.AvatarObjectKey;
        user.ClearAvatarMedia();
        await _users.UpdateAsync(user, ct);
        if (!string.IsNullOrWhiteSpace(previousKey))
            await _storage.DeleteObjectAsync(previousKey, ct);

        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<ProfileDto>> RemoveBannerAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        var previousKey = user.BannerObjectKey;
        user.ClearBannerMedia();
        await _users.UpdateAsync(user, ct);
        if (!string.IsNullOrWhiteSpace(previousKey))
            await _storage.DeleteObjectAsync(previousKey, ct);

        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<ProfileDto>> UpdateAvatarFallbackColorAsync(
        Guid userId, string color, CancellationToken ct = default)
    {
        if (!IsSafeHexColor(color))
            return Result<ProfileDto>.Fail("Validation.Failed", "Avatar fallback color must be a hex color.");

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        user.UpdateAvatarFallbackColor(color);
        await _users.UpdateAsync(user, ct);
        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<ProfileDto>> UpdateBannerFallbackColorAsync(
        Guid userId, string color, CancellationToken ct = default)
    {
        if (!IsSafeHexColor(color))
            return Result<ProfileDto>.Fail("Validation.Failed", "Banner fallback color must be a hex color.");

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        user.UpdateBannerFallbackColor(color);
        await _users.UpdateAsync(user, ct);
        return await BuildDtoAsync(user, ct);
    }

    public async Task<Result<UsernameChangeResultDto>> UpdateUsernameAsync(Guid userId, string newUsername, CancellationToken ct = default)
    {
        var normalized = newUsername.Trim().ToUpperInvariant();
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<UsernameChangeResultDto>.Fail("General.NotFound", "User not found.");

        if (user.NormalizedUsername == normalized)
            return Result<UsernameChangeResultDto>.Fail("Profile.SameUsername", "The requested username is the same as your current one.");

        if (await _users.ExistsByUsernameAsync(normalized, ct))
            return Result<UsernameChangeResultDto>.Fail("Profile.UsernameTaken", "That username is already in use.");

        var now = DateTime.UtcNow;
        if (!user.HasUsedImmediateUsernameChangeIn(now.Year, now.Month))
        {
            user.UpdateUsername(newUsername);
            user.RecordImmediateUsernameChange(now.Year, now.Month);
            await _users.UpdateAsync(user, ct);

            return Result<UsernameChangeResultDto>.Ok(new(
                AppliedImmediately: true,
                Message: "Username changed. You have used this month's immediate username change.",
                Request: null));
        }

        var requestResult = await UpsertUsernameRequestAsync(user, newUsername, now.Year, now.Month, ct);
        if (!requestResult.IsSuccess)
            return Result<UsernameChangeResultDto>.Fail(requestResult.Error!.Code, requestResult.Error.Message);

        return Result<UsernameChangeResultDto>.Ok(new(
            AppliedImmediately: false,
            Message: "Username change request saved for admin review.",
            Request: requestResult.Value));
    }

    public async Task<Result<IReadOnlyList<ExternalLinkDto>>> GetLinksAsync(
        Guid userId, CancellationToken ct = default)
    {
        var links = await _profiles.GetLinksByUserIdAsync(userId, ct);
        return Result<IReadOnlyList<ExternalLinkDto>>.Ok(links.Select(ToLinkDto).ToList());
    }

    public async Task<Result<IReadOnlyList<ExternalLinkDto>>> UpdateLinksAsync(
        Guid userId, UpdateLinksRequestDto request, CancellationToken ct = default)
    {
        List<ProfileExternalLink> newLinks;
        try
        {
            newLinks = request.Links
                .Select((l, i) => ProfileExternalLink.Create(userId, l.Platform, l.Url, l.DisplayLabel, i))
                .ToList();
        }
        catch (ArgumentException ex)
        {
            return Result<IReadOnlyList<ExternalLinkDto>>.Fail("Validation.Failed", ex.Message);
        }

        await _profiles.ReplaceLinksAsync(userId, newLinks, ct);
        return Result<IReadOnlyList<ExternalLinkDto>>.Ok(newLinks.Select(ToLinkDto).ToList());
    }

    public async Task<Result<IReadOnlyList<string>>> GetInterestsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var tags = await _profiles.GetInterestsByUserIdAsync(userId, ct);
        return Result<IReadOnlyList<string>>.Ok(tags.Select(t => t.Name).ToList());
    }

    public async Task<Result<IReadOnlyList<string>>> UpdateInterestsAsync(
        Guid userId, UpdateInterestsRequestDto request, CancellationToken ct = default)
    {
        var tags = request.Interests
            .Select(name => ProfileInterestTag.Create(userId, name))
            .ToList();

        await _profiles.ReplaceInterestsAsync(userId, tags, ct);
        return Result<IReadOnlyList<string>>.Ok(tags.Select(t => t.Name).ToList());
    }

    public async Task<Result<UsernameChangeRequestDto>> RequestUsernameChangeAsync(
        Guid userId, string requestedUsername, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
            return Result<UsernameChangeRequestDto>.Fail("General.NotFound", "User not found.");

        var normalized = requestedUsername.Trim().ToUpperInvariant();
        if (user.NormalizedUsername == normalized)
            return Result<UsernameChangeRequestDto>.Fail("Profile.SameUsername",
                "The requested username is the same as your current one.");

        if (await _users.ExistsByUsernameAsync(normalized, ct))
            return Result<UsernameChangeRequestDto>.Fail("Profile.UsernameTaken",
                "That username is already in use.");

        var now = DateTime.UtcNow;
        return await UpsertUsernameRequestAsync(user, requestedUsername, now.Year, now.Month, ct);
    }

    public async Task<Result<UsernameChangeRequestDto?>> GetUsernameChangeRequestAsync(
        Guid userId, CancellationToken ct = default)
    {
        var request = await _usernameRequests.GetLatestByUserIdAsync(userId, ct);
        return Result<UsernameChangeRequestDto?>.Ok(request is null ? null : ToRequestDto(request));
    }

    public async Task<Result<UsernameChangeRequestDto>> CancelUsernameChangeRequestAsync(
        Guid userId, CancellationToken ct = default)
    {
        var request = await _usernameRequests.GetPendingByUserIdAsync(userId, ct);
        if (request is null || request.UserId != userId)
            return Result<UsernameChangeRequestDto>.Fail("Profile.UsernameRequestNotFound", "No pending username change request was found.");

        request.Cancel();
        await _usernameRequests.UpdateAsync(request, ct);
        return Result<UsernameChangeRequestDto>.Ok(ToRequestDto(request));
    }

    private async Task<Result<UsernameChangeRequestDto>> UpsertUsernameRequestAsync(
        User user, string requestedUsername, int year, int month, CancellationToken ct)
    {
        var existingPending = await _usernameRequests.GetPendingByUserIdAsync(user.Id, ct);
        if (existingPending is not null)
        {
            existingPending.UpdateRequestedUsername(requestedUsername);
            await _usernameRequests.UpdateAsync(existingPending, ct);
            return Result<UsernameChangeRequestDto>.Ok(ToRequestDto(existingPending));
        }

        if (user.HasUsedAdminUsernameRequestIn(year, month) ||
            await _usernameRequests.GetByUserIdAndMonthAsync(user.Id, year, month, ct) is not null)
            return Result<UsernameChangeRequestDto>.Fail("Profile.MonthlyAdminRequestUsed",
                "You have already used this month's username change request.");

        var request = UsernameChangeRequest.Create(user.Id, requestedUsername, year, month);
        user.RecordAdminUsernameRequest(year, month);
        await _users.UpdateAsync(user, ct);
        await _usernameRequests.AddAsync(request, ct);
        return Result<UsernameChangeRequestDto>.Ok(ToRequestDto(request));
    }

    private async Task<Result<ProfileMediaUploadUrlDto>> CreateUploadUrlAsync(
        Guid userId,
        ProfileMediaUploadUrlRequestDto request,
        string mediaKind,
        long maxBytes,
        CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileMediaUploadUrlDto>.Fail("General.NotFound", "User not found.");

        if (!AllowedImageTypes.Contains(request.ContentType))
            return Result<ProfileMediaUploadUrlDto>.Fail("Validation.Failed", "Only JPEG, PNG, and WebP images are accepted.");
        if (request.FileSizeBytes <= 0 || request.FileSizeBytes > maxBytes)
            return Result<ProfileMediaUploadUrlDto>.Fail("Validation.Failed", $"{mediaKind} image exceeds the allowed size.");

        var extension = ExtensionForContentType(request.ContentType);
        if (extension is null)
            return Result<ProfileMediaUploadUrlDto>.Fail("Validation.Failed", "Unsupported image type.");

        var objectKey = $"{_storageOptions.ProfilePrefix.Trim('/')}/users/{userId}/{mediaKind}/{Guid.NewGuid():N}{extension}";
        var expires = DateTime.UtcNow.AddMinutes(_storageOptions.UploadUrlExpiryMinutes);
        var uploadUrl = await _storage.CreatePresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            TimeSpan.FromMinutes(_storageOptions.UploadUrlExpiryMinutes),
            ct);

        return Result<ProfileMediaUploadUrlDto>.Ok(new(uploadUrl, objectKey, request.ContentType, expires));
    }

    private async Task<Result<ProfileDto>> ConfirmUploadAsync(
        Guid userId,
        string objectKey,
        string mediaKind,
        CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result<ProfileDto>.Fail("General.NotFound", "User not found.");

        var expectedPrefix = $"{_storageOptions.ProfilePrefix.Trim('/')}/users/{userId}/{mediaKind}/";
        if (!objectKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
            return Result<ProfileDto>.Fail("Validation.Failed", "The uploaded object does not belong to the current user.");

        if (!await _storage.ObjectExistsAsync(objectKey, ct))
            return Result<ProfileDto>.Fail("Profile.MediaNotFound", "Uploaded media was not found in storage.");

        var previousKey = mediaKind == "avatar" ? user.AvatarObjectKey : user.BannerObjectKey;
        if (mediaKind == "avatar")
            user.SetAvatarMedia(objectKey);
        else
            user.SetBannerMedia(objectKey);

        await _users.UpdateAsync(user, ct);

        if (!string.IsNullOrWhiteSpace(previousKey) &&
            !string.Equals(previousKey, objectKey, StringComparison.Ordinal))
            await _storage.DeleteObjectAsync(previousKey, ct);

        return await BuildDtoAsync(user, ct);
    }

    private async Task<Result<ProfileDto>> BuildDtoAsync(User user, CancellationToken ct)
    {
        var links = await _profiles.GetLinksByUserIdAsync(user.Id, ct);
        var interests = await _profiles.GetInterestsByUserIdAsync(user.Id, ct);
        var friendCount = await _friends.GetFriendCountAsync(user.Id, ct);
        return Result<ProfileDto>.Ok(await ToDtoAsync(user, links, interests, friendCount, ct));
    }

    private static UsernameChangeRequestDto ToRequestDto(UsernameChangeRequest r) => new(
        r.Id, r.RequestedUsername, r.Status.ToString(),
        r.RejectionReason, r.RequestYear, r.RequestMonth, r.CreatedAt, r.UpdatedAt, r.CancelledAt, r.ReviewedAt,
        CanEdit: r.Status == UsernameChangeStatus.Pending,
        CanCancel: r.Status == UsernameChangeStatus.Pending);

    private async Task<ProfileDto> ToDtoAsync(
        User user,
        IReadOnlyList<ProfileExternalLink> links,
        IReadOnlyList<ProfileInterestTag> interests,
        int friendCount,
        CancellationToken ct)
    {
        var readExpiry = TimeSpan.FromMinutes(_storageOptions.ReadUrlExpiryMinutes);
        var avatarUrl = !string.IsNullOrWhiteSpace(user.AvatarObjectKey)
            ? await _storage.CreatePresignedReadUrlAsync(user.AvatarObjectKey, readExpiry, ct)
            : user.AvatarUrl;
        var bannerUrl = !string.IsNullOrWhiteSpace(user.BannerObjectKey)
            ? await _storage.CreatePresignedReadUrlAsync(user.BannerObjectKey, readExpiry, ct)
            : user.BannerUrl;

        return new ProfileDto(
            UserId: user.Id,
            Username: user.Username,
            DisplayName: user.DisplayName,
            Bio: user.Bio,
            AvatarUrl: avatarUrl,
            BannerUrl: bannerUrl,
            HasUploadedAvatar: !string.IsNullOrWhiteSpace(user.AvatarObjectKey),
            HasUploadedBanner: !string.IsNullOrWhiteSpace(user.BannerObjectKey),
            StatusMessage: user.StatusMessage,
            Region: string.Equals(user.Region, "eu-west", StringComparison.OrdinalIgnoreCase) ? string.Empty : user.Region,
            Color: user.Color,
            BannerFallbackColor: user.BannerFallbackColor,
            Initials: user.Initials,
            Visibility: user.Visibility.ToString(),
            ProfileType: user.ProfileType.ToString(),
            Role: user.Role.ToString(),
            Level: user.Level,
            Elo: user.Elo,
            FriendCount: friendCount,
            JoinedAt: user.CreatedAt,
            Links: links.Select(ToLinkDto).ToList(),
            Interests: interests.Select(t => t.Name).ToList());
    }

    private static ExternalLinkDto ToLinkDto(ProfileExternalLink l) => new(
        l.Id, l.Platform, l.Url, l.DisplayLabel, l.SortOrder);

    private static string? ExtensionForContentType(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => null
        };

    private static bool IsSafeHexColor(string color) =>
        System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");
}
