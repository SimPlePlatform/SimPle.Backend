using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.DTOs;
using SimPle.Application.Matchmaking.Services;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

/// <summary>
/// The Module 6 Quick Match ticket surface (slice 6C).
///
/// <para>
/// Every route requires authentication, and the actor is always the JWT <c>sub</c> claim — no endpoint accepts a
/// caller-supplied <c>userId</c> or rating. Every state-changing endpoint additionally requires the
/// <c>X-Requested-With: XMLHttpRequest</c> CSRF header, matching the rest of the API.
/// </para>
///
/// <para>
/// <strong>Privacy-safe not-found (OWASP API1:2023).</strong> Another user's ticket id returns <c>404</c>, never
/// <c>403</c> — a 403 would confirm the id exists. A missing ticket and a foreign one are indistinguishable.
/// </para>
///
/// <para>
/// <strong>These endpoints work before Module 8.</strong> Enqueue, poll, and cancel are fully functional today; the
/// matching worker is what waits for a match runtime. A ticket therefore queues, widens through its rating bands,
/// and honestly times out — and <c>dependencyReadiness</c> on every response is how the client knows that without
/// hardcoding it.
/// </para>
/// </summary>
[ApiController]
[Route("api/matchmaking")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class MatchmakingController : ControllerBase
{
    private readonly IMatchmakingService _matchmaking;

    public MatchmakingController(IMatchmakingService matchmaking)
    {
        _matchmaking = matchmaking;
    }

    [HttpPost("tickets")]
    [EnableRateLimiting("matchmaking-enqueue")]
    [SwaggerOperation(
        Summary = "Enqueue a Quick Match ticket",
        Description = "Idempotent by pool key: re-sending the identical ticket returns the live one rather than " +
                      "minting a second. A *different* ticket while one is live is 409 Matchmaking.AlreadyQueued, " +
                      "as is enqueueing while already in a lobby — a user holds one active lobby OR one active " +
                      "ticket, never both. The rating is a server-side snapshot (provisional 1200 until Module 10); " +
                      "a client cannot supply one and so cannot choose its own opponents.",
        OperationId = "Matchmaking_CreateTicket", Tags = new[] { "Matchmaking" })]
    [ProducesResponseType(typeof(TicketDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateTicket([FromBody] CreateTicketRequestDto request, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _matchmaking.EnqueueAsync(userId, request, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpGet("tickets/{ticketId:guid}")]
    [EnableRateLimiting("matchmaking-status")]
    [SwaggerOperation(
        Summary = "Poll a Quick Match ticket's status",
        Description = "Polled every 2 seconds until Module 7 supplies live delivery. `currentBand` is the rating " +
                      "half-width the ticket accepts right now (100 → 200 → 400 as it ages, null once it has " +
                      "reached its deadline), so the UI can show the search visibly widening. Another user's " +
                      "ticket id is a privacy-safe 404.",
        OperationId = "Matchmaking_GetTicket", Tags = new[] { "Matchmaking" })]
    [ProducesResponseType(typeof(TicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetTicket([FromRoute] Guid ticketId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _matchmaking.GetTicketAsync(userId, ticketId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpDelete("tickets/{ticketId:guid}")]
    [EnableRateLimiting("matchmaking-write")]
    [SwaggerOperation(
        Summary = "Cancel a Quick Match ticket",
        Description = "Returns 200 with the ticket's current status. A cancel that arrives after a worker has " +
                      "claimed the ticket is deliberately NOT an error — the user did nothing wrong and the queue " +
                      "simply got there first, so they receive the ticket's real state rather than a failure.",
        OperationId = "Matchmaking_CancelTicket", Tags = new[] { "Matchmaking" })]
    [ProducesResponseType(typeof(TicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelTicket([FromRoute] Guid ticketId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _matchmaking.CancelAsync(userId, ticketId, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string CsrfHeader = "X-Requested-With";
    private const string CsrfHeaderValue = "XMLHttpRequest";

    private bool HasCsrfHeader() =>
        string.Equals(Request.Headers[CsrfHeader], CsrfHeaderValue, StringComparison.Ordinal);

    private IActionResult MissingCsrfHeader() => BadRequest(new ApiErrorResponse(new ApiErrorDetail(
        "Auth.CsrfHeaderRequired", $"The {CsrfHeader} header is required for this request.")));

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    /// <summary>
    /// Maps the matchmaking error catalogue to HTTP. Enqueue shares several codes with the lobby surface — a user
    /// who is already in a lobby, a disabled capability, a spent retry budget — and they are mapped identically
    /// here, because they mean the same thing and a client should not have to learn two vocabularies for one module.
    /// </summary>
    private IActionResult MapError(Error error)
    {
        var body = new ApiErrorResponse(new ApiErrorDetail(error.Code, error.Message, error.RetryAfterUtc));

        switch (error.Code)
        {
            case MatchmakingErrors.TicketNotFound:
                return NotFound(body);

            case LobbyErrors.Forbidden:
            case LobbyErrors.Blocked:
                return StatusCode(StatusCodes.Status403Forbidden, body);

            case MatchmakingErrors.AlreadyQueued:
            case MatchmakingErrors.TicketExpired:
            case LobbyErrors.AlreadyActive:
            case LobbyErrors.CapabilityDisabled:
            case LobbyErrors.ConcurrencyConflict:
                return Conflict(body);

            case MatchmakingErrors.RuntimeUnavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, body);

            case LobbyErrors.RateLimitExceeded:
                if (error.RetryAfterUtc is DateTime until)
                {
                    var seconds = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                    Response.Headers.RetryAfter = seconds.ToString();
                }
                return StatusCode(StatusCodes.Status429TooManyRequests, body);

            default:
                // Validation.Failed, Auth.CsrfHeaderRequired.
                return BadRequest(body);
        }
    }
}
