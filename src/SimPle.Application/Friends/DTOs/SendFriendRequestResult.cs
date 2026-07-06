namespace SimPle.Application.Friends.DTOs;

/// <summary>
/// Outcome discriminator for POST /api/friends/requests. The controller maps <see cref="Outcome"/> to the
/// HTTP status: <c>request_created</c> → 201, everything else → 200 (reconciliation R1/R2).
/// </summary>
public sealed record SendFriendRequestResult(string Outcome, FriendRequestDto Request)
{
    public const string RequestCreated = "request_created";
    public const string AlreadyPending = "already_pending";
    public const string CrossRequestAccepted = "cross_request_accepted";
}
