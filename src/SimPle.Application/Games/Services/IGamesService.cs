using SimPle.Application.Games.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.Games.Services;

/// <summary>
/// A successful detail read is exactly one of <see cref="Game"/> (200) or <see cref="Tombstone"/> (410 — the
/// slug exists but is Retired). <see cref="ETag"/> is populated only alongside <see cref="Game"/>: the spec's
/// cache headers apply to 200/304 responses only, never to 410.
/// </summary>
public sealed record GameDetailResult(GameCatalogDto? Game, GameTombstoneDto? Tombstone, string? ETag);

/// <summary>A successful featured read: <see cref="Game"/> is null when no game currently holds FeaturedRank 1 (204).</summary>
public sealed record FeaturedResult(GameCatalogDto? Game, string? ETag);

public sealed record CatalogPageResult(CursorPage<GameCatalogDto> Page, string ETag);

public interface IGamesService
{
    Task<Result<CatalogPageResult>> ListAsync(
        string? query,
        IReadOnlyList<string>? category,
        IReadOnlyList<string>? tag,
        IReadOnlyList<string>? mode,
        IReadOnlyList<string>? lifecycle,
        string? sort,
        int limit,
        string? cursor,
        CancellationToken ct = default);

    Task<Result<GameDetailResult>> GetDetailAsync(string slug, CancellationToken ct = default);

    Task<Result<FeaturedResult>> GetFeaturedAsync(CancellationToken ct = default);

    Task<Result<CursorPage<GameFavoriteDto>>> GetFavoritesAsync(
        Guid userId, int limit, string? cursor, CancellationToken ct = default);

    Task<Result<GameFavoriteDto>> PutFavoriteAsync(Guid userId, string slug, CancellationToken ct = default);

    Task<Result> DeleteFavoriteAsync(Guid userId, string slug, CancellationToken ct = default);
}
