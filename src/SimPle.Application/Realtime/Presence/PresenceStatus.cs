namespace SimPle.Application.Realtime.Presence;

/// <summary>
/// Ephemeral, in-memory-only presence status (docs/specs/module-07-realtime-presence-chat-spec.md, "Domain
/// Invariants: presence precedence"). Ordinal value is precedence for Max-based aggregation across a user's
/// connections — never persisted, never confused with the pre-existing persisted <c>User.Status</c>
/// (<c>SimPle.Domain.Users.UserStatus</c>), which is a different field with different semantics.
/// </summary>
public enum PresenceStatus
{
    Offline = 0,
    Away = 1,
    Online = 2,
    InLobby = 3,
    Playing = 4,
}
