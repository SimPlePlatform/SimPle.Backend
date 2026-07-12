using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimPle.Api.Models;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace SimPle.Api.Controllers;

/// <summary>
/// Rematch: creates a new <em>lobby</em> (which Module 6 owns) addressed off the terminal match it replays (which
/// Module 8 will own).
///
/// <para>
/// This lives in its own controller with a real <c>api/matches</c> prefix rather than as an absolute-route action
/// hanging off <see cref="LobbiesController"/>. The absolute form works at runtime but
/// <c>scripts/check-contract-drift.mjs</c> statically concatenates the controller prefix with the action template,
/// and recorded the route as <c>POST /api/lobbies//api/matches/*/rematch-lobbies</c>. The frontend slice would then
/// call the real path, find no matching route in the inventory, and trip a false drift failure on a gate that is
/// supposed to catch exactly this class of mistake. A correctly-prefixed controller keeps the tooling honest.
/// </para>
///
/// <para>
/// Module 8 is free to add its own <c>MatchesController</c> for match resources; ASP.NET routes by template, not
/// by class, so two controllers may share the prefix as long as their action templates do not collide.
/// </para>
/// </summary>
[ApiController]
[Route("api/matches")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public sealed class MatchRematchController : ControllerBase
{
    private readonly ILobbiesService _lobbies;

    public MatchRematchController(ILobbiesService lobbies)
    {
        _lobbies = lobbies;
    }

    [HttpPost("{terminalMatchId:guid}/rematch-lobbies")]
    [EnableRateLimiting("lobby-create")]
    [SwaggerOperation(
        Summary = "Create a rematch lobby from a finished match",
        Description = "Returns 503 Lobbies.MatchRuntimeUnavailable — Module 8 owns match records, so there is no " +
                      "terminal match to read the prior settings and participants from, and inventing a lobby from " +
                      "defaults would produce one that silently is not the rematch it claims to be. When M8 lands, " +
                      "nobody is auto-joined or auto-readied by a rematch and blocks are re-checked.",
        OperationId = "Lobbies_CreateRematch", Tags = new[] { "Lobbies" })]
    [ProducesResponseType(typeof(CreateLobbyResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateRematch([FromRoute] Guid terminalMatchId, CancellationToken ct)
    {
        if (!HasCsrfHeader())
        {
            return BadRequest(new ApiErrorResponse(new ApiErrorDetail(
                "Auth.CsrfHeaderRequired", "The X-Requested-With header is required for this request.")));
        }

        if (!Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
            return Unauthorized();

        var result = await _lobbies.CreateRematchLobbyAsync(userId, terminalMatchId, ct);
        if (!result.IsSuccess)
        {
            var body = new ApiErrorResponse(new ApiErrorDetail(result.Error!.Code, result.Error.Message));
            return result.Error.Code == LobbyErrors.MatchRuntimeUnavailable
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, body)
                : BadRequest(body);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    private bool HasCsrfHeader() =>
        string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.Ordinal);
}
