namespace SimPle.Application.Realtime.Presence;

/// <summary>
/// Resolves who is authorized to receive a <c>PresenceChanged</c> broadcast for a given subject right now
/// (docs/specs/module-07-realtime-presence-chat-spec.md, "Presence visibility (approved)").
///
/// Only the lobby-co-member case has a real UI consumer today (<c>LobbyPage.tsx</c>'s seat presence dot) — the
/// broader Public/FriendsOnly fan-out the approved policy describes has no caller yet (no friends-list or
/// profile-page presence indicator exists), so this resolver deliberately narrows to what is actually rendered:
/// the subject themself (their own sidebar/topbar/dashboard avatar updates live) plus their current lobby's
/// other joined members, minus anyone blocked in either direction. Extending this to the full policy is a new
/// UI feature, not a bug fix, and should be scoped and approved separately.
/// </summary>
public interface IPresenceViewerResolver
{
    Task<IReadOnlyList<Guid>> ResolveAsync(Guid subjectUserId, CancellationToken ct = default);
}
