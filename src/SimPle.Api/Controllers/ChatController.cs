using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimPle.Api.Models;
using SimPle.Application.Chat;
using SimPle.Application.Realtime.Contracts;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

/// <summary>
/// The Module 7 chat REST surface (docs/specs/module-07-realtime-presence-chat-spec.md, "REST"). Only history and
/// delete are REST routes — sending a message is hub-only (<c>RealtimeHub.SendLobbyMessage</c>); there is no REST
/// send endpoint in the approved API contract, so none is added here.
///
/// Follows <see cref="LobbiesController"/>'s conventions: the actor is always the JWT <c>sub</c> claim, state-
/// changing routes require the <c>X-Requested-With: XMLHttpRequest</c> CSRF header, and privacy-safe 404s collapse
/// missing/unauthorized/nonexistent into the identical <see cref="ChatErrors.NotFound"/>.
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chat;

    public ChatController(IChatService chat)
    {
        _chat = chat;
    }

    [HttpGet("lobbies/{lobbyId:guid}/messages")]
    [SwaggerOperation(
        Summary = "Lobby chat history (keyset cursor paged)",
        Description = "limit default 30, cap 50. Ordered (createdAt, id). direction: before (scrollback, default) " +
                      "| after (reconnect repair). A private or foreign lobby is a privacy-safe Chat.NotFound.",
        OperationId = "Chat_GetHistory", Tags = new[] { "Chat" })]
    [ProducesResponseType(typeof(CursorPage<ChatMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(
        [FromRoute] Guid lobbyId,
        [FromQuery] string? cursor,
        [FromQuery] string direction = "before",
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        if (!Enum.TryParse<ChatHistoryDirection>(direction, ignoreCase: true, out var parsedDirection))
            return BadRequest(Error(ChatErrors.ValidationFailed, "direction must be 'before' or 'after'."));

        Response.Headers.CacheControl = "private, no-store";
        var result = await _chat.GetHistoryAsync(userId, lobbyId, parsedDirection, cursor, limit, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpDelete("messages/{messageId:guid}")]
    [SwaggerOperation(
        Summary = "Author: delete a chat message",
        Description = "Produces a tombstone (body cleared, Deleted=true) and fans out ChatMessageDeleted. A retried " +
                      "delete on an already-deleted message is idempotent and replays the original DeletedAtUtc.",
        OperationId = "Chat_DeleteMessage", Tags = new[] { "Chat" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMessage([FromRoute] Guid messageId, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var result = await _chat.DeleteAsync(userId, messageId, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
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

    /// <summary>Maps the M07-B2 chat error catalogue to HTTP. Only <see cref="ChatErrors.Forbidden"/> (not the
    /// author on delete) is a 403; everything else that would otherwise disclose existence collapses to 404.</summary>
    private IActionResult MapError(Error error)
    {
        var body = new ApiErrorResponse(new ApiErrorDetail(error.Code, error.Message, error.RetryAfterUtc));

        switch (error.Code)
        {
            case ChatErrors.NotFound:
                return NotFound(body);

            case ChatErrors.Forbidden:
                return StatusCode(StatusCodes.Status403Forbidden, body);

            case ChatErrors.RateLimitExceeded:
                if (error.RetryAfterUtc is DateTime until)
                {
                    var seconds = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                    Response.Headers.RetryAfter = seconds.ToString();
                }
                return StatusCode(StatusCodes.Status429TooManyRequests, body);

            default:
                // Chat.InvalidBody, Chat.ProfanityRejected, Chat.MessageExpired, Validation.Failed,
                // Pagination.InvalidCursor, Auth.CsrfHeaderRequired.
                return BadRequest(body);
        }
    }

    private static ApiErrorResponse Error(string code, string message) =>
        new(new ApiErrorDetail(code, message));
}
