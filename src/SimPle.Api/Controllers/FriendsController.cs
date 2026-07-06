using System.IdentityModel.Tokens.Jwt;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Friends.DTOs;
using SimPle.Application.Friends.Services;
using SimPle.Application.Friends.Validators;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

[ApiController]
[Route("api/friends")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class FriendsController : ControllerBase
{
    private readonly IFriendsService _friends;

    public FriendsController(IFriendsService friends)
    {
        _friends = friends;
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    [HttpGet("summary")]
    [SwaggerOperation(Summary = "Get friend/request counts summary",
        OperationId = "Friends_GetSummary", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(FriendSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _friends.GetSummaryAsync(userId, ct);
        return Ok(result.Value);
    }

    // ── Friend list (keyset cursor) ─────────────────────────────────────────────

    [HttpGet]
    [SwaggerOperation(Summary = "Get the authenticated user's friend list (keyset cursor paged)",
        OperationId = "Friends_GetList", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(CursorPage<FriendDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetFriends(
        [FromQuery] string? query,
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.GetFriendsAsync(userId, query, limit, cursor, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Requests (keyset cursor) ────────────────────────────────────────────────

    [HttpGet("requests")]
    [SwaggerOperation(Summary = "Get incoming or outgoing friend requests (keyset cursor paged)",
        OperationId = "Friends_GetRequests", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(CursorPage<FriendRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRequests(
        [FromQuery] string direction = "incoming",
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (direction != "incoming" && direction != "outgoing")
            return BadRequest(Error("Validation.Failed", "direction must be 'incoming' or 'outgoing'."));

        var result = await _friends.GetRequestsAsync(userId, direction, limit, cursor, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPost("requests")]
    [EnableRateLimiting("friend-send")]
    [SwaggerOperation(Summary = "Send a friend request (or atomically accept a reverse pending request)",
        OperationId = "Friends_SendRequest", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(SendFriendRequestResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SendFriendRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendFriendRequest([FromBody] SendFriendRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new SendFriendRequestValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _friends.SendFriendRequestAsync(userId, request.TargetUserId, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        // request_created → 201 Created; already_pending / cross_request_accepted → 200 OK.
        return result.Value!.Outcome == SendFriendRequestResult.RequestCreated
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Ok(result.Value);
    }

    [HttpPost("requests/{id:guid}/accept")]
    [SwaggerOperation(Summary = "Accept a friend request",
        OperationId = "Friends_AcceptRequest", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(FriendRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptFriendRequest([FromRoute] Guid id, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.AcceptFriendRequestAsync(userId, id, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPost("requests/{id:guid}/decline")]
    [SwaggerOperation(Summary = "Decline a friend request",
        OperationId = "Friends_DeclineRequest", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(FriendRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeclineFriendRequest([FromRoute] Guid id, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.DeclineFriendRequestAsync(userId, id, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpDelete("requests/{id:guid}")]
    [SwaggerOperation(Summary = "Cancel an outgoing pending friend request",
        OperationId = "Friends_CancelRequest", Tags = new[] { "Friends" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelFriendRequest([FromRoute] Guid id, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.CancelFriendRequestAsync(userId, id, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    // ── Remove friend ─────────────────────────────────────────────────────────

    [HttpDelete("{friendUserId:guid}")]
    [SwaggerOperation(Summary = "Remove an accepted friend",
        OperationId = "Friends_Remove", Tags = new[] { "Friends" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveFriend([FromRoute] Guid friendUserId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.RemoveFriendAsync(userId, friendUserId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    // ── Suggestions / Discovery / Dismissal ─────────────────────────────────────

    [HttpGet("suggestions")]
    [EnableRateLimiting("friend-suggestions")]
    [SwaggerOperation(Summary = "Get friend suggestions with mutual friend counts",
        OperationId = "Friends_GetSuggestions", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(IReadOnlyList<FriendSuggestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetSuggestions(
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.GetSuggestionsAsync(userId, limit, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpGet("discovery")]
    [EnableRateLimiting("friend-discovery")]
    [SwaggerOperation(Summary = "Look up a single user by exact username for adding as a friend",
        OperationId = "Friends_Discover", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(DiscoveryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Discover(
        [FromQuery] string username,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!IsValidUsername(username))
            return BadRequest(Error("Validation.Failed", "username must be a valid handle (3-30 chars: letters, digits, _ . -)."));

        var result = await _friends.DiscoverByUsernameAsync(userId, username, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPut("suggestions/{userId:guid}/dismiss")]
    [EnableRateLimiting("friend-suggestions")]
    [SwaggerOperation(Summary = "Dismiss a friend suggestion (suppressed for 30 days)",
        OperationId = "Friends_DismissSuggestion", Tags = new[] { "Friends" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DismissSuggestion([FromRoute] Guid userId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var actorId)) return Unauthorized();

        var result = await _friends.DismissSuggestionAsync(actorId, userId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    // ── Blocks (keyset cursor) ──────────────────────────────────────────────────

    [HttpGet("blocks")]
    [SwaggerOperation(Summary = "Get the authenticated user's block list (keyset cursor paged)",
        OperationId = "Friends_GetBlocks", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(CursorPage<BlockDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetBlocks(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.GetBlocksAsync(userId, limit, cursor, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPost("blocks")]
    [EnableRateLimiting("friend-block")]
    [SwaggerOperation(Summary = "Block a user",
        OperationId = "Friends_BlockUser", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(BlockUserResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BlockUserResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> BlockUser([FromBody] BlockUserRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new BlockUserValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _friends.BlockUserAsync(userId, request.TargetUserId, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        // blocked → 201 Created; already_blocked → 200 OK.
        return result.Value!.Outcome == BlockUserResult.Blocked
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Ok(result.Value);
    }

    [HttpDelete("blocks/{blockedUserId:guid}")]
    [EnableRateLimiting("friend-block")]
    [SwaggerOperation(Summary = "Unblock a user",
        OperationId = "Friends_UnblockUser", Tags = new[] { "Friends" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> UnblockUser([FromRoute] Guid blockedUserId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _friends.UnblockUserAsync(userId, blockedUserId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [HttpGet("settings")]
    [SwaggerOperation(Summary = "Get friend request privacy settings",
        OperationId = "Friends_GetSettings", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(FriendSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _friends.GetSettingsAsync(userId, ct);
        return Ok(result.Value);
    }

    [HttpPut("settings")]
    [SwaggerOperation(Summary = "Update friend request privacy settings",
        OperationId = "Friends_UpdateSettings", Tags = new[] { "Friends" })]
    [ProducesResponseType(typeof(FriendSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateFriendSettingsRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var validator = new UpdateFriendSettingsValidator();
        var validation = await validator.ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
            return BadRequest(Error("Validation.Failed", validation.Errors.First().ErrorMessage));

        var result = await _friends.UpdateSettingsAsync(userId, request.FriendRequestPrivacy, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string CsrfHeader = "X-Requested-With";
    private const string CsrfHeaderValue = "XMLHttpRequest";

    // Discovery username: mirrors the M1 username validator (3-30 chars, letters/digits/_.-).
    private static readonly Regex UsernamePattern =
        new(@"^[a-zA-Z0-9_.-]{3,30}$", RegexOptions.Compiled);

    private static bool IsValidUsername(string? username) =>
        !string.IsNullOrWhiteSpace(username) && UsernamePattern.IsMatch(username.Trim());

    private bool HasCsrfHeader() =>
        string.Equals(Request.Headers[CsrfHeader], CsrfHeaderValue, StringComparison.Ordinal);

    private IActionResult MissingCsrfHeader() => BadRequest(Error(
        "Auth.CsrfHeaderRequired",
        $"The {CsrfHeader} header is required for this request."));

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    /// <summary>
    /// Maps a domain <see cref="Error"/> to the canonical HTTP status (reconciliation R12). BOLA/privacy
    /// denials surface as 404 (never 403). Cooldown carries <c>Retry-After</c> + body <c>retryAfterUtc</c>.
    /// </summary>
    private IActionResult MapError(Error error)
    {
        var body = new ApiErrorResponse(new ApiErrorDetail(error.Code, error.Message, error.RetryAfterUtc));
        switch (error.Code)
        {
            case "Profile.NotVisible":
                return NotFound(body);

            case "Friends.RequestCooldown":
                if (error.RetryAfterUtc is DateTime until)
                {
                    var seconds = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                    Response.Headers.RetryAfter = seconds.ToString();
                }
                return Conflict(body);

            case "Friends.AlreadyFriends":
            case "Friends.ConcurrencyConflict":
                return Conflict(body);

            default:
                // SelfRequest, SelfBlock, RequestsDisabled, NotFriendOfFriend, NotPending,
                // Pagination.InvalidCursor, Validation.Failed, Auth.CsrfHeaderRequired → 400.
                return BadRequest(body);
        }
    }

    private static ApiErrorResponse Error(string code, string message) =>
        new(new ApiErrorDetail(code, message));
}
