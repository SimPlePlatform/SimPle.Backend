using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Services;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

/// <summary>
/// The Module 6 lobby command surface.
///
/// Every route requires authentication, and the actor is always the JWT <c>sub</c> claim — no endpoint accepts a
/// caller-supplied <c>hostId</c>/<c>userId</c>. Every state-changing endpoint additionally requires the
/// <c>X-Requested-With: XMLHttpRequest</c> CSRF header, matching the rest of the API.
///
/// <para>
/// <strong>Privacy-safe not-found (OWASP API1:2023).</strong> A lobby, invite, or credential the caller is not
/// entitled to reach returns <c>404</c>, never <c>403</c> — a 403 would confirm the id exists. Missing, expired,
/// private-and-unauthorized, and another user's ids are all deliberately indistinguishable. <c>403</c> is reserved
/// for a caller who is <em>already a visible member</em> and merely lacks the host role, where nothing is leaked
/// by saying so.
/// </para>
///
/// <para>
/// <strong>A join credential is never a resource identifier.</strong> It travels in a request body on exactly one
/// route (<c>POST /api/lobbies/join</c>) and is returned in exactly two places (create and rotate). No path, query
/// string, log line, or event ever carries it.
/// </para>
/// </summary>
[ApiController]
[Route("api/lobbies")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class LobbiesController : ControllerBase
{
    private readonly ILobbiesService _lobbies;

    public LobbiesController(ILobbiesService lobbies)
    {
        _lobbies = lobbies;
    }

    // ── Create & read ────────────────────────────────────────────────────────

    [HttpPost]
    [EnableRateLimiting("lobby-create")]
    [SwaggerOperation(
        Summary = "Create a lobby and issue its join code + share link",
        Description = "The plaintext join code and link token are returned here and at rotation only — they are " +
                      "stored as keyed digests and appear in no other response, log, or event.",
        OperationId = "Lobbies_Create", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(CreateLobbyResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create([FromBody] CreateLobbyRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.CreateAsync(userId, request, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpGet]
    [EnableRateLimiting("lobby-read")]
    [SwaggerOperation(
        Summary = "Browse open public lobbies (keyset cursor paged)",
        Description = "Private, expired, full, and blocked lobbies never appear and never affect totals or cursors.",
        OperationId = "Lobbies_GetPublic", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(CursorPage<LobbySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPublic(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.GetPublicAsync(userId, limit, cursor, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpGet("capabilities/{gameSlug}")]
    [EnableRateLimiting("lobby-read")]
    [SwaggerOperation(
        Summary = "Get the active capability profile for a game",
        Description = "The pinned-version source (D2): capabilityVersion plus the allowed modes, time controls, " +
                      "tie-break rules, and spectator policies a client reads before create-lobby or a matchmaking " +
                      "ticket create, instead of guessing values the server will reject.",
        OperationId = "Lobbies_GetCapabilityProfile", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(GameCapabilityProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCapabilityProfile([FromRoute] string gameSlug, CancellationToken ct)
    {
        if (!TryGetUserId(out _)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.GetCapabilityProfileAsync(gameSlug, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpGet("{lobbyId:guid}")]
    [EnableRateLimiting("lobby-read")]
    [SwaggerOperation(
        Summary = "Get a lobby the caller may see",
        Description = "Members see the full lobby. Non-members see an open public lobby. Everything else is a " +
                      "privacy-safe 404 — a private or foreign lobby is indistinguishable from one that does not exist.",
        OperationId = "Lobbies_Get", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Get([FromRoute] Guid lobbyId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.GetAsync(userId, lobbyId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpGet("me/invites")]
    [EnableRateLimiting("lobby-read")]
    [SwaggerOperation(Summary = "The caller's pending, unexpired lobby invites",
        OperationId = "Lobbies_GetMyInvites", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(IReadOnlyList<LobbyInviteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetMyInvites(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.GetMyInvitesAsync(userId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpGet("me/active")]
    [EnableRateLimiting("lobby-read")]
    [SwaggerOperation(
        Summary = "The caller's active lobby or matchmaking ticket",
        Description = "At most one of the two is ever set — that is the one-active-lobby-or-ticket invariant.",
        OperationId = "Lobbies_GetMyActive", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(ActiveContextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetMyActive(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.GetMyActiveAsync(userId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Join ─────────────────────────────────────────────────────────────────

    [HttpPost("join")]
    [EnableRateLimiting("lobby-join")]
    [SwaggerOperation(
        Summary = "Join a lobby by join code, share link token, or (for a Public+Open lobby) its id",
        Description = "Exactly one of code, linkToken, or lobbyId. The credential forms are supplied in the body, " +
                      "never in the path — a credential is a secret, not a resource id. A wrong, expired, rotated, " +
                      "revoked, or closed-lobby credential all return the identical Lobbies.CredentialInvalid, so " +
                      "this endpoint is not an oracle; failed credential attempts are throttled specifically. " +
                      "lobbyId carries no throttle and is only honored when the target is currently Public and " +
                      "Open — the same visibility rule as the public browse listing — so a private, foreign, or " +
                      "non-open lobby's id answers with the identical privacy-safe Lobbies.NotFound.",
        OperationId = "Lobbies_Join", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Join([FromBody] JoinLobbyRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.JoinByCredentialAsync(userId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Membership ───────────────────────────────────────────────────────────

    [HttpPost("{lobbyId:guid}/leave")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(
        Summary = "Leave a lobby",
        Description = "If the host leaves, hosting transfers to the longest-tenured eligible member (tie-broken by " +
                      "user id); the lobby closes when none exists.",
        OperationId = "Lobbies_Leave", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Leave([FromRoute] Guid lobbyId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.LeaveAsync(userId, lobbyId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    [HttpPut("{lobbyId:guid}/ready")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(
        Summary = "Set the caller's own readiness",
        Description = "The host is implicitly ready and cannot un-ready.",
        OperationId = "Lobbies_SetReadiness", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetReadiness(
        [FromRoute] Guid lobbyId, [FromBody] SetReadinessRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.SetReadinessAsync(userId, lobbyId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPatch("{lobbyId:guid}/settings")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(
        Summary = "Host: change match settings",
        Description = "Validated against the pinned capability profile before persistence. Every match-affecting " +
                      "change resets readiness for all joined non-host members; privacy and spectator policy do not.",
        OperationId = "Lobbies_UpdateSettings", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSettings(
        [FromRoute] Guid lobbyId, [FromBody] UpdateLobbySettingsRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.UpdateSettingsAsync(userId, lobbyId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPost("{lobbyId:guid}/kick")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(Summary = "Host: remove a member from the lobby",
        OperationId = "Lobbies_Kick", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Kick(
        [FromRoute] Guid lobbyId, [FromBody] KickMemberRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.KickAsync(userId, lobbyId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Credential rotation ──────────────────────────────────────────────────

    [HttpPost("{lobbyId:guid}/credential/rotate")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(
        Summary = "Host: rotate the join code and share link",
        Description = "The previous code and link are invalidated immediately — there is no window in which both work.",
        OperationId = "Lobbies_RotateCredential", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyCredentialDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RotateCredential([FromRoute] Guid lobbyId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _lobbies.RotateCredentialAsync(userId, lobbyId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Invites ──────────────────────────────────────────────────────────────

    [HttpPost("{lobbyId:guid}/invites")]
    [EnableRateLimiting("lobby-invite")]
    [SwaggerOperation(
        Summary = "Host: invite a friend to the lobby",
        Description = "Accepted friends only. This is what stops the invite endpoint being a way to reveal a " +
                      "private lobby's existence to an arbitrary user id.",
        OperationId = "Lobbies_CreateInvite", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyInviteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateInvite(
        [FromRoute] Guid lobbyId, [FromBody] CreateInviteRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.CreateInviteAsync(userId, lobbyId, request, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpDelete("{lobbyId:guid}/invites/{inviteId:guid}")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(Summary = "Host: revoke a pending invite",
        OperationId = "Lobbies_RevokeInvite", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeInvite(
        [FromRoute] Guid lobbyId, [FromRoute] Guid inviteId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.RevokeInviteAsync(userId, lobbyId, inviteId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    /// <remarks>
    /// Reconciliation <strong>R6</strong> — a route the approved spec's table omits.
    ///
    /// Without it an invitee cannot reach a seat at all: join takes a credential, and handing every invitee the
    /// lobby's private code would both leak it and leave a revoked invite still redeemable. Accept runs the same
    /// bounded transaction as a credential join, so an invited member is admitted under identical block, capacity,
    /// and one-active-lobby-or-ticket rules.
    /// </remarks>
    [HttpPost("invites/{inviteId:guid}/accept")]
    [EnableRateLimiting("lobby-join")]
    [SwaggerOperation(
        Summary = "Accept a lobby invite and take a seat",
        Description = "Enforces the same blocks, capacity, and one-active-lobby-or-ticket rules as a credential " +
                      "join — an invite is permission to try, never a bypass. Another user's invite id is a " +
                      "privacy-safe 404.",
        OperationId = "Lobbies_AcceptInvite", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(LobbyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptInvite([FromRoute] Guid inviteId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.AcceptInviteAsync(userId, inviteId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    /// <remarks>Reconciliation <strong>R6</strong>, with <c>accept</c> above.</remarks>
    [HttpPost("invites/{inviteId:guid}/decline")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(Summary = "Decline a lobby invite",
        OperationId = "Lobbies_DeclineInvite", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeclineInvite([FromRoute] Guid inviteId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.DeclineInviteAsync(userId, inviteId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
    }

    // ── Start ────────────────────────────────────────────────────────────────

    [HttpPost("{lobbyId:guid}/start")]
    [EnableRateLimiting("lobby-write")]
    [SwaggerOperation(
        Summary = "Host: start the match",
        Description = "Returns 503 Lobbies.MatchRuntimeUnavailable while Module 8 is not registered — the lobby " +
                      "stays Open and no room is created. A committed match request is a durable request, never a " +
                      "created match.",
        OperationId = "Lobbies_Start", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(StartLobbyResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(
        [FromRoute] Guid lobbyId, [FromBody] StartLobbyRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _lobbies.StartAsync(userId, lobbyId, request, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        // 202, not 200: the lobby has entered Starting and a request is durable, but the match does not exist yet
        // and will not until M8 answers. A 200 would imply the work is done.
        return Accepted(result.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string CsrfHeader = "X-Requested-With";
    private const string CsrfHeaderValue = "XMLHttpRequest";

    private bool HasCsrfHeader() =>
        string.Equals(Request.Headers[CsrfHeader], CsrfHeaderValue, StringComparison.Ordinal);

    private IActionResult MissingCsrfHeader() => BadRequest(Error(
        "Auth.CsrfHeaderRequired",
        $"The {CsrfHeader} header is required for this request."));

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    /// <summary>
    /// Maps the Module 6 error catalogue to HTTP.
    ///
    /// <c>Lobbies.NotFound</c> and <c>Lobbies.CredentialInvalid</c> both become <c>404</c> — the credential case
    /// deliberately included, so a wrong code is indistinguishable from a lobby that does not exist. Only
    /// <c>Lobbies.Forbidden</c> (a visible member lacking the host role) is a <c>403</c>; nothing is leaked by it.
    /// </summary>
    private IActionResult MapError(Error error)
    {
        var body = new ApiErrorResponse(new ApiErrorDetail(error.Code, error.Message, error.RetryAfterUtc));

        switch (error.Code)
        {
            case LobbyErrors.NotFound:
            case LobbyErrors.CredentialInvalid:
            case LobbyErrors.CapabilityNotFound:
                return NotFound(body);

            case LobbyErrors.Forbidden:
            case LobbyErrors.Blocked:
                return StatusCode(StatusCodes.Status403Forbidden, body);

            case LobbyErrors.Full:
            case LobbyErrors.Closed:
            case LobbyErrors.Expired:
            case LobbyErrors.StaleRevision:
            case LobbyErrors.AlreadyActive:
            case LobbyErrors.CapabilityDisabled:
            case LobbyErrors.ConcurrencyConflict:
            case LobbyErrors.NotStartable:
                return Conflict(body);

            case LobbyErrors.MatchRuntimeUnavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, body);

            case LobbyErrors.RateLimitExceeded:
                if (error.RetryAfterUtc is DateTime until)
                {
                    var seconds = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                    Response.Headers.RetryAfter = seconds.ToString();
                }
                return StatusCode(StatusCodes.Status429TooManyRequests, body);

            default:
                // Validation.Failed, Pagination.InvalidCursor, Lobbies.InvalidTarget, Auth.CsrfHeaderRequired.
                return BadRequest(body);
        }
    }

    private static ApiErrorResponse Error(string code, string message) =>
        new(new ApiErrorDetail(code, message));
}
