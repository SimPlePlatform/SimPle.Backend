using SimPle.Application.Games;
using SimPle.Domain.Games;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Fully normalized/validated catalog query, built by <see cref="SimPle.Application.Games.Services.GamesService"/>
/// and consumed by <c>GameRepository</c>. <see cref="AfterSortKey"/>/<see cref="AfterSlug"/> are the decoded
/// keyset position (null on the first page); their string shape is defined by <see cref="GameCatalogSortKey"/>.
/// </summary>
public sealed record GameCatalogFilter(
    string? NormalizedSearch,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Modes,
    IReadOnlyList<GameLifecycle> Lifecycles,
    string Sort,
    int Limit,
    string? AfterSortKey,
    string? AfterSlug);

/// <summary>
/// Data access for the game catalog and favorites. List/detail/featured reads are budgeted at exactly two SQL
/// round trips: <see cref="GetCatalogPageAsync"/>/<see cref="GetBySlugAsync"/>/<see cref="GetFeaturedAsync"/>
/// fetch the game row(s) only, and <see cref="GetTagsAndCapabilitiesAsync"/> fetches tags and mode
/// capabilities for those ids in a single hand-written UNION ALL query (EF's <c>AsSplitQuery</c> would cost a
/// third round trip, which the spec's performance budget forbids).
/// </summary>
public interface IGameRepository
{
    Task<IReadOnlyList<Game>> GetCatalogPageAsync(GameCatalogFilter filter, CancellationToken ct = default);

    /// <summary>Flat (GameId, Kind, Value) rows for the given ids, Kind is "tag" or "mode".</summary>
    Task<IReadOnlyList<(Guid GameId, string Kind, string Value)>> GetTagsAndCapabilitiesAsync(
        IReadOnlyList<Guid> gameIds, CancellationToken ct = default);

    /// <summary>Any lifecycle, including Draft/Retired — the service decides visibility (404 vs 410).</summary>
    Task<Game?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>The single FeaturedRank = 1 game, or null when none exists (204 case).</summary>
    Task<Game?> GetFeaturedAsync(CancellationToken ct = default);

    // ── Favorites ────────────────────────────────────────────────────────────

    /// <summary>Active favorites ordered by (UpdatedAt DESC, Id DESC).</summary>
    Task<IReadOnlyList<(UserFavoriteGame Favorite, Game Game)>> GetFavoritesPageAsync(
        Guid userId, int limit, DateTime? afterFavoritedAt, Guid? afterId, CancellationToken ct = default);

    Task<UserFavoriteGame?> GetFavoriteAsync(Guid userId, Guid gameId, CancellationToken ct = default);

    /// <summary>Brand-new (UserId, GameId) row. Conflict = a concurrent first favorite already won the race.</summary>
    Task<AddFavoriteOutcome> AddFavoriteAsync(UserFavoriteGame favorite, OutboxMessage evt, CancellationToken ct = default);

    /// <summary>
    /// Toggles an existing row (Refavorite/Unfavorite already called by the caller). ConcurrencyConflict means
    /// the outbox's unique (AggregateId, EventType, AggregateDomainVersion) index rejected a retried transition
    /// or a racing writer already applied it — the caller re-reads and converges.
    /// </summary>
    Task<UpdateFavoriteOutcome> UpdateFavoriteAsync(UserFavoriteGame favorite, OutboxMessage evt, CancellationToken ct = default);
}
