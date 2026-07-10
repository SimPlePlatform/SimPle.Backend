using SimPle.Application.Profiles.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.Profiles.Services;

public interface IProfileService
{
    Task<Result<ProfileDto>> GetMyProfileAsync(Guid userId, CancellationToken ct = default);
    Task<Result<ProfileDto>> GetPublicProfileAsync(string username, Guid? requesterId, CancellationToken ct = default);
    Task<Result<ProfileViewerContextDto>> GetViewerContextAsync(string username, Guid viewerId, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="username"/>'s accepted friends, privacy-filtered against <paramref name="viewerId"/>
    /// (null = anonymous, allowed only when the target is publicly viewable with an Everyone friends-list).
    /// Collapses to <c>Profile.NotVisible</c> whenever the drill-down is not permitted.
    /// </summary>
    Task<Result<CursorPage<PublicIdentityDto>>> GetFriendsListAsync(
        string username, Guid? viewerId, string? query, int limit, string? cursor, CancellationToken ct = default);

    /// <summary>Friends common to <paramref name="viewerId"/> and <paramref name="username"/>. Always authenticated.</summary>
    Task<Result<CursorPage<PublicIdentityDto>>> GetMutualFriendsListAsync(
        string username, Guid viewerId, int limit, string? cursor, CancellationToken ct = default);
    Task<Result<ProfileDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request, CancellationToken ct = default);
    Task<Result<ProfileMediaUploadUrlDto>> CreateAvatarUploadUrlAsync(Guid userId, ProfileMediaUploadUrlRequestDto request, CancellationToken ct = default);
    Task<Result<ProfileMediaUploadUrlDto>> CreateBannerUploadUrlAsync(Guid userId, ProfileMediaUploadUrlRequestDto request, CancellationToken ct = default);
    Task<Result<ProfileDto>> ConfirmAvatarUploadAsync(Guid userId, string objectKey, CancellationToken ct = default);
    Task<Result<ProfileDto>> ConfirmBannerUploadAsync(Guid userId, string objectKey, CancellationToken ct = default);
    Task<Result<ProfileDto>> RemoveAvatarAsync(Guid userId, CancellationToken ct = default);
    Task<Result<ProfileDto>> RemoveBannerAsync(Guid userId, CancellationToken ct = default);
    Task<Result<ProfileDto>> UpdateAvatarFallbackColorAsync(Guid userId, string color, CancellationToken ct = default);
    Task<Result<ProfileDto>> UpdateBannerFallbackColorAsync(Guid userId, string color, CancellationToken ct = default);
    Task<Result<UsernameChangeResultDto>> UpdateUsernameAsync(Guid userId, string newUsername, CancellationToken ct = default);
    Task<Result<UsernameChangeRequestDto>> RequestUsernameChangeAsync(Guid userId, string requestedUsername, CancellationToken ct = default);
    Task<Result<UsernameChangeRequestDto?>> GetUsernameChangeRequestAsync(Guid userId, CancellationToken ct = default);
    Task<Result<UsernameChangeRequestDto>> CancelUsernameChangeRequestAsync(Guid userId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ExternalLinkDto>>> GetLinksAsync(Guid userId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ExternalLinkDto>>> UpdateLinksAsync(Guid userId, UpdateLinksRequestDto request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> GetInterestsAsync(Guid userId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> UpdateInterestsAsync(Guid userId, UpdateInterestsRequestDto request, CancellationToken ct = default);
}
