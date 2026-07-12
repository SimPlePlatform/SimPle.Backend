namespace SimPle.Domain.Lobbies;

/// <summary>
/// Lobby lifecycle: <c>Open -&gt; Starting -&gt; Started</c>, or <c>Open|Starting -&gt; Closed|Expired</c>.
/// <c>Starting</c> is entered only when a match request is atomically committed while the M8 readiness probe is
/// healthy; <c>Started</c> only after M8 returns <c>MatchCreatedV1</c>. Started/Closed/Expired are terminal and
/// reject every mutation.
/// </summary>
public enum LobbyState
{
    Open,
    Starting,
    Started,
    Closed,
    Expired,
}

public enum LobbyPrivacy
{
    Public,
    Private,
}

public enum SpectatorPolicy
{
    Anyone,
    FriendsOnly,
    Disabled,
}

/// <summary>Auditable reason a lobby reached a terminal closed state.</summary>
public enum LobbyClosedReason
{
    HostLeft,
    NoEligibleHost,
    HostClosed,
    Expired,
    HostSuspended,
}

/// <summary>Membership lifecycle. Readiness is a separate boolean, never a member state.</summary>
public enum LobbyMemberState
{
    Joined,
    Left,
    Kicked,
}

public enum LobbyInviteState
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

/// <summary>A rotated or revoked credential is dead immediately; only <c>Active</c> can be redeemed.</summary>
public enum LobbyCredentialState
{
    Active,
    Rotated,
    Revoked,
}

public enum LobbyStartRequestState
{
    Open,
    Succeeded,
    Failed,
}
