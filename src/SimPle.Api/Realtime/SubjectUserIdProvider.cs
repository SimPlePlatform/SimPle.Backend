using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.SignalR;

namespace SimPle.Api.Realtime;

/// <summary>
/// Maps SignalR's notion of "user" to the JWT <c>sub</c> claim, matching every other consumer of the access
/// token cookie in this codebase (<c>Program.cs</c> uses <c>options.MapInboundClaims = false</c>, so the claim
/// type stays the literal <c>"sub"</c> rather than being remapped to <c>ClaimTypes.NameIdentifier</c>). This is
/// what makes <c>Clients.User(userId)</c> and <c>HubCallerContext.UserIdentifier</c> resolve to the same value
/// used everywhere else (<see cref="Guid"/> parsed from the claim).
/// </summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
}
