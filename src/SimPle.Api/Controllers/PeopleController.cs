using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.People.DTOs;
using SimPle.Application.People.Services;
using SimPle.Shared.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

[ApiController]
[Route("api/people")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class PeopleController : ControllerBase
{
    private readonly IPeopleService _people;

    public PeopleController(IPeopleService people)
    {
        _people = people;
    }

    [HttpGet("search")]
    [EnableRateLimiting("people-search")]
    [SwaggerOperation(Summary = "Bounded people search by username/display name prefix (keyset cursor paged)",
        OperationId = "People_Search", Tags = new[] { "People" })]
    [ProducesResponseType(typeof(CursorPage<PeopleSearchResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new ApiErrorResponse(new ApiErrorDetail(
                "Validation.Failed", "Search query must be between 2 and 100 characters.")));

        Response.Headers.CacheControl = "private, no-store";
        var result = await _people.SearchAsync(userId, q, limit, cursor, ct);
        return result.IsSuccess ? Ok(result.Value) : MapError(result.Error!);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private IActionResult MapError(Error error) =>
        BadRequest(new ApiErrorResponse(new ApiErrorDetail(error.Code, error.Message)));
}
