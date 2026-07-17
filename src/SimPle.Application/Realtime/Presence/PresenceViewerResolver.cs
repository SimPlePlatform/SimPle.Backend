using SimPle.Application.Common.Interfaces;

namespace SimPle.Application.Realtime.Presence;

/// <summary>
/// Self + current lobby co-members, block-filtered in either direction. See <see cref="IPresenceViewerResolver"/>
/// for why the broader Public/FriendsOnly policy is not implemented here.
/// </summary>
public sealed class PresenceViewerResolver : IPresenceViewerResolver
{
    private readonly ILobbyRepository _lobbies;

    public PresenceViewerResolver(ILobbyRepository lobbies)
    {
        _lobbies = lobbies;
    }

    public async Task<IReadOnlyList<Guid>> ResolveAsync(Guid subjectUserId, CancellationToken ct = default)
    {
        var lobby = await _lobbies.GetActiveLobbyForUserAsync(subjectUserId, ct);
        if (lobby is null)
            return new[] { subjectUserId };

        var coMemberIds = lobby.JoinedMembers
            .Select(m => m.UserId)
            .Where(id => id != subjectUserId)
            .ToList();

        if (coMemberIds.Count == 0)
            return new[] { subjectUserId };

        var blocked = await _lobbies.GetBlockedCounterpartsAsync(subjectUserId, coMemberIds, ct);
        var blockedSet = blocked.Count == 0 ? null : new HashSet<Guid>(blocked);

        var viewers = new List<Guid>(coMemberIds.Count + 1) { subjectUserId };
        foreach (var id in coMemberIds)
        {
            if (blockedSet is null || !blockedSet.Contains(id))
                viewers.Add(id);
        }

        return viewers;
    }
}
