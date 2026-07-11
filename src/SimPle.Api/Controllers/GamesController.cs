using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Games.DTOs;
using SimPle.Application.Games.Services;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

/// <summary>
/// Public catalog reads (list/detail/featured) are anonymous, auth-independent, and cache-headered per spec
/// deviation D1 (<c>Vary: Cookie</c>, not <c>Vary: Authorization</c> — this app authenticates via cookie only,
/// see <c>ProfileController.cs:112-115</c>). Favorites are authenticated, ownership-scoped from the JWT
/// <c>sub</c> claim only (never a request body id), and never cached.
/// </summary>
[ApiController]
[Route("api/games")]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class GamesController : ControllerBase
{
    private readonly IGamesService _games;

    public GamesController(IGamesService games)
    {
        _games = games;
    }

    // ── Public catalog reads ────────────────────────────────────────────────

    [HttpGet]
    [EnableRateLimiting("catalog-read")]
    [SwaggerOperation(Summary = "List/search the game catalog (keyset cursor paged)",
        OperationId = "Games_List", Tags = new[] { "Games" })]
    [ProducesResponseType(typeof(CursorPage<GameCatalogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> List(
        [FromQuery] string? query,
        [FromQuery] string[]? category,
        [FromQuery] string[]? tag,
        [FromQuery] string[]? mode,
        [FromQuery] string[]? lifecycle,
        [FromQuery] string? sort,
        [FromQuery] int limit = 24,
        [FromQuery] string? after = null,
        CancellationToken ct = default)
    {
        var result = await _games.ListAsync(query, category, tag, mode, lifecycle, sort, limit, after, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        var (page, etag) = result.Value!;
        return IfNoneMatchMatches(etag) ? NotModified(etag) : CachedOk(page, etag);
    }

    [HttpGet("featured")]
    [EnableRateLimiting("catalog-read")]
    [SwaggerOperation(Summary = "Get the single featured game, or none",
        OperationId = "Games_GetFeatured", Tags = new[] { "Games" })]
    [ProducesResponseType(typeof(GameCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetFeatured(CancellationToken ct)
    {
        var result = await _games.GetFeaturedAsync(ct);
        var featured = result.Value!;
        if (featured.Game is null) return NoContent();

        return IfNoneMatchMatches(featured.ETag!) ? NotModified(featured.ETag!) : CachedOk(featured.Game, featured.ETag!);
    }

    [HttpGet("{slug}")]
    [EnableRateLimiting("catalog-read")]
    [SwaggerOperation(Summary = "Get a single game by slug",
        OperationId = "Games_GetBySlug", Tags = new[] { "Games" })]
    [ProducesResponseType(typeof(GameCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(GameTombstoneDto), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetBySlug([FromRoute] string slug, CancellationToken ct)
    {
        var result = await _games.GetDetailAsync(slug, ct);
        if (!result.IsSuccess) return MapError(result.Error!);

        var detail = result.Value!;
        // 410 carries the minimal tombstone DTO directly (not the ApiErrorResponse envelope) per the spec's
        // DTOs section; cache headers apply only to 200/304, never to 410.
        if (detail.Tombstone is not null) return StatusCode(StatusCodes.Status410Gone, detail.Tombstone);

        return IfNoneMatchMatches(detail.ETag!) ? NotModified(detail.ETag!) : CachedOk(detail.Game, detail.ETag!);
    }

    // ── Authenticated favorites ─────────────────────────────────────────────

    [HttpGet("me/favorites")]
    [Authorize]
    [EnableRateLimiting("game-favorites")]
    [SwaggerOperation(Summary = "List the authenticated user's active favorites (keyset cursor paged)",
        OperationId = "Games_GetFavorites", Tags = new[] { "Games" })]
    [ProducesResponseType(typeof(CursorPage<GameFavoriteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetFavorites(
        [FromQuery] int limit = 24, [FromQuery] string? after = null, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _games.GetFavoritesAsync(userId, limit, after, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpPut("me/favorites/{slug}")]
    [Authorize]
    [EnableRateLimiting("game-favorites")]
    [SwaggerOperation(Summary = "Favorite a game (idempotent — identical DTO on repeat calls)",
        OperationId = "Games_PutFavorite", Tags = new[] { "Games" })]
    [ProducesResponseType(typeof(GameFavoriteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> PutFavorite([FromRoute] string slug, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _games.PutFavoriteAsync(userId, slug, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    [HttpDelete("me/favorites/{slug}")]
    [Authorize]
    [EnableRateLimiting("game-favorites")]
    [SwaggerOperation(Summary = "Unfavorite a game (idempotent — 204 whether present, absent, or already inactive)",
        OperationId = "Games_DeleteFavorite", Tags = new[] { "Games" })]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DeleteFavorite([FromRoute] string slug, CancellationToken ct)
    {
        if (!HasCsrfHeader()) return MissingCsrfHeader();
        if (!TryGetUserId(out var userId)) return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        var result = await _games.DeleteFavoriteAsync(userId, slug, ct);
        return result.IsSuccess ? NoContent() : MapError(result.Error!);
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

    private bool IfNoneMatchMatches(string etag)
    {
        var header = Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrEmpty(header)) return false;
        return header.Split(',').Select(v => v.Trim()).Any(v => v == etag || v == "*");
    }

    private IActionResult NotModified(string etag)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=60";
        Response.Headers.Vary = "Cookie";
        return StatusCode(StatusCodes.Status304NotModified);
    }

    private IActionResult CachedOk<T>(T value, string etag)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=60";
        Response.Headers.Vary = "Cookie";
        return Ok(value);
    }

    /// <summary>Games.NotFound -> 404, Games.Retired -> 409 (new-favorite rejection only; the detail-read
    /// 410 tombstone path never reaches this switch), everything else (Validation.Failed,
    /// Pagination.InvalidCursor) -> 400.</summary>
    private IActionResult MapError(Error error) =>
        error.Code switch
        {
            "Games.NotFound" => NotFound(Error(error.Code, error.Message)),
            "Games.Retired" => Conflict(Error(error.Code, error.Message)),
            _ => BadRequest(Error(error.Code, error.Message)),
        };

    private static ApiErrorResponse Error(string code, string message) =>
        new(new ApiErrorDetail(code, message));
}
