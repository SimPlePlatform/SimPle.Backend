using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Profiles.DTOs;
using SimPle.Application.Profiles.Services;
using SimPle.Application.Profiles.Validators;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

[ApiController]
[Route("api/profile")]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class ProfileController : ControllerBase
{
    private readonly IProfileService _profile;

    public ProfileController(IProfileService profile)
    {
        _profile = profile;
    }

    // ── Current user profile ──────────────────────────────────────────────────

    [HttpGet("me")]
    [Authorize]
    [SwaggerOperation(Summary = "Get the authenticated user's full profile",
        OperationId = "Profile_GetMe", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyProfile(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _profile.GetMyProfileAsync(userId, ct);
        if (!result.IsSuccess) return NotFound(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPut("me")]
    [Authorize]
    [EnableRateLimiting("profile-update")]
    [SwaggerOperation(Summary = "Update the authenticated user's profile",
        OperationId = "Profile_UpdateMe", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateProfileRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateProfileRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.UpdateProfileAsync(userId, request, ct);
        if (!result.IsSuccess) return NotFound(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPut("me/username")]
    [Authorize]
    [EnableRateLimiting("profile-username")]
    [SwaggerOperation(Summary = "Change the authenticated user's username/handle",
        OperationId = "Profile_UpdateUsername", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(UsernameChangeResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateUsername([FromBody] UpdateUsernameRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateUsernameRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.UpdateUsernameAsync(userId, request.Username, ct);
        if (!result.IsSuccess)
        {
            if (result.Error!.Code == "Profile.UsernameTaken") return Conflict(Error(result.Error.Code, result.Error.Message));
            return BadRequest(Error(result.Error.Code, result.Error.Message));
        }
        return Ok(result.Value);
    }

    // ── Public profile ────────────────────────────────────────────────────────

    [HttpGet("{username}")]
    [SwaggerOperation(Summary = "Get a public profile by username",
        OperationId = "Profile_GetPublic", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicProfile([FromRoute] string username, CancellationToken ct)
    {
        Guid? requesterId = null;
        if (TryGetUserId(out var id)) requesterId = id;

        var result = await _profile.GetPublicProfileAsync(username, requesterId, ct);
        if (!result.IsSuccess)
        {
            return result.Error!.Code switch
            {
                "General.NotFound" => NotFound(Error(result.Error.Code, result.Error.Message)),
                _ => StatusCode(StatusCodes.Status403Forbidden, Error(result.Error.Code, result.Error.Message))
            };
        }
        return Ok(result.Value);
    }

    // ── Avatar / banner upload ────────────────────────────────────────────────

    [HttpPost("me/avatar/upload-url")]
    [Authorize]
    [SwaggerOperation(Summary = "Create a presigned upload URL for an avatar image",
        OperationId = "Profile_CreateAvatarUploadUrl", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileMediaUploadUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAvatarUploadUrl(
        [FromBody] ProfileMediaUploadUrlRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.CreateAvatarUploadUrlAsync(userId, request, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPost("me/avatar/confirm")]
    [Authorize]
    [SwaggerOperation(Summary = "Confirm an uploaded avatar image and attach it to the profile",
        OperationId = "Profile_ConfirmAvatarUpload", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmAvatarUpload(
        [FromBody] ConfirmProfileMediaUploadRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.ConfirmAvatarUploadAsync(userId, request.ObjectKey, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpDelete("me/avatar")]
    [Authorize]
    [SwaggerOperation(Summary = "Remove the authenticated user's avatar image",
        OperationId = "Profile_RemoveAvatar", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveAvatar(CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.RemoveAvatarAsync(userId, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPut("me/avatar/fallback")]
    [Authorize]
    [SwaggerOperation(Summary = "Update avatar fallback color",
        OperationId = "Profile_UpdateAvatarFallback", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateAvatarFallback(
        [FromBody] UpdateAvatarFallbackRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.UpdateAvatarFallbackColorAsync(userId, request.Color, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPut("me/banner/fallback")]
    [Authorize]
    [SwaggerOperation(Summary = "Update banner fallback color",
        OperationId = "Profile_UpdateBannerFallback", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateBannerFallback(
        [FromBody] UpdateBannerFallbackRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.UpdateBannerFallbackColorAsync(userId, request.Color, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPost("me/banner/upload-url")]
    [Authorize]
    [SwaggerOperation(Summary = "Create a presigned upload URL for a banner/cover image",
        OperationId = "Profile_CreateBannerUploadUrl", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileMediaUploadUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateBannerUploadUrl(
        [FromBody] ProfileMediaUploadUrlRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.CreateBannerUploadUrlAsync(userId, request, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpPost("me/banner/confirm")]
    [Authorize]
    [SwaggerOperation(Summary = "Confirm an uploaded banner/cover image and attach it to the profile",
        OperationId = "Profile_ConfirmBannerUpload", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmBannerUpload(
        [FromBody] ConfirmProfileMediaUploadRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.ConfirmBannerUploadAsync(userId, request.ObjectKey, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    [HttpDelete("me/banner")]
    [Authorize]
    [SwaggerOperation(Summary = "Remove the authenticated user's banner/cover image",
        OperationId = "Profile_RemoveBanner", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveBanner(CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.RemoveBannerAsync(userId, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    // ── Username change request ───────────────────────────────────────────────

    [HttpPost("me/username-change-request")]
    [Authorize]
    [EnableRateLimiting("profile-username")]
    [SwaggerOperation(Summary = "Submit a request to change username (requires admin approval)",
        OperationId = "Profile_RequestUsernameChange", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(UsernameChangeRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestUsernameChange(
        [FromBody] UpdateUsernameRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateUsernameRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.RequestUsernameChangeAsync(userId, request.Username, ct);
        if (!result.IsSuccess)
        {
            if (result.Error!.Code is "Profile.UsernameTaken" or "Profile.PendingRequestExists" or "Profile.MonthlyAdminRequestUsed")
                return Conflict(Error(result.Error.Code, result.Error.Message));
            return BadRequest(Error(result.Error.Code, result.Error.Message));
        }
        return CreatedAtAction(nameof(GetMyUsernameChangeRequest), result.Value);
    }

    [HttpGet("me/username-change-request")]
    [Authorize]
    [SwaggerOperation(Summary = "Get the current user's latest username change request",
        OperationId = "Profile_GetUsernameChangeRequest", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(UsernameChangeRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyUsernameChangeRequest(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _profile.GetUsernameChangeRequestAsync(userId, ct);
        if (result.Value is null) return NoContent();
        return Ok(result.Value);
    }

    [HttpPut("me/username-change-request")]
    [Authorize]
    [SwaggerOperation(Summary = "Edit the current pending username change request",
        OperationId = "Profile_EditUsernameChangeRequest", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(UsernameChangeRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> EditUsernameChangeRequest(
        [FromBody] UpdateUsernameRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateUsernameRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.RequestUsernameChangeAsync(userId, request.Username, ct);
        if (!result.IsSuccess)
        {
            if (result.Error!.Code is "Profile.UsernameTaken" or "Profile.MonthlyAdminRequestUsed")
                return Conflict(Error(result.Error.Code, result.Error.Message));
            return BadRequest(Error(result.Error.Code, result.Error.Message));
        }
        return Ok(result.Value);
    }

    [HttpDelete("me/username-change-request")]
    [Authorize]
    [SwaggerOperation(Summary = "Cancel the current pending username change request",
        OperationId = "Profile_CancelUsernameChangeRequest", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(UsernameChangeRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelUsernameChangeRequest(CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _profile.CancelUsernameChangeRequestAsync(userId, ct);
        if (!result.IsSuccess) return BadRequest(Error(result.Error!.Code, result.Error.Message));
        return Ok(result.Value);
    }

    // ── Links ────────────────────────────────────────────────────────────────

    [HttpGet("me/links")]
    [Authorize]
    [SwaggerOperation(Summary = "Get the authenticated user's external links",
        OperationId = "Profile_GetLinks", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(IReadOnlyList<ExternalLinkDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLinks(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _profile.GetLinksAsync(userId, ct);
        return Ok(result.Value);
    }

    [HttpPut("me/links")]
    [Authorize]
    [SwaggerOperation(Summary = "Replace the authenticated user's external links",
        OperationId = "Profile_UpdateLinks", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(IReadOnlyList<ExternalLinkDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateLinks([FromBody] UpdateLinksRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateLinksRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.UpdateLinksAsync(userId, request, ct);
        return Ok(result.Value);
    }

    // ── Interests ─────────────────────────────────────────────────────────────

    [HttpGet("me/interests")]
    [Authorize]
    [SwaggerOperation(Summary = "Get the authenticated user's game interest tags",
        OperationId = "Profile_GetInterests", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetInterests(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _profile.GetInterestsAsync(userId, ct);
        return Ok(result.Value);
    }

    [HttpPut("me/interests")]
    [Authorize]
    [SwaggerOperation(Summary = "Replace the authenticated user's game interest tags",
        OperationId = "Profile_UpdateInterests", Tags = new[] { "Profile" })]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateInterests([FromBody] UpdateInterestsRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateInterestsRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _profile.UpdateInterestsAsync(userId, request, ct);
        return Ok(result.Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string CsrfHeader = "X-Requested-With";
    private const string CsrfHeaderValue = "XMLHttpRequest";

    private bool HasCsrfHeader() =>
        string.Equals(Request.Headers[CsrfHeader], CsrfHeaderValue, StringComparison.Ordinal);

    private IActionResult MissingCsrfHeader() => BadRequest(Error(
        "Auth.CsrfHeaderRequired",
        $"The {CsrfHeader} header is required for this request."));

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static ApiErrorResponse Error(string code, string message) =>
        new(new ApiErrorDetail(code, message));
}
