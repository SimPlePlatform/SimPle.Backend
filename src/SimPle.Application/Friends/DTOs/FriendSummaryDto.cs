namespace SimPle.Application.Friends.DTOs;

public sealed record FriendSummaryDto(
    int FriendCount,
    int IncomingRequestCount,
    int OutgoingRequestCount);
