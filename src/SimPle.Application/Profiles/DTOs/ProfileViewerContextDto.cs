namespace SimPle.Application.Profiles.DTOs;

/// <summary>
/// Server-derived relationship/action context for the authenticated viewer of a profile. RelationshipState
/// is exactly one of Self | None | IncomingPending | OutgoingPending | Friends | BlockedBySelf.
/// BlockedByTarget is never a public state — it 404s the same as a nonexistent profile.
/// </summary>
public sealed record ProfileViewerContextDto(
    string RelationshipState,
    int VisibleMutualFriendCount,
    bool CanViewFriends,
    int? VisibleFriendCount,
    IReadOnlyList<string> AllowedActions);
