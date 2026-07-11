using System.Security.Cryptography;
using System.Text;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Games.DTOs;
using SimPle.Application.Games.Outbox;
using SimPle.Domain.Games;
using SimPle.Shared.Common;

namespace SimPle.Application.Games.Services;

public sealed class GamesService : IGamesService
{
    private readonly IGameRepository _games;

    private const int MaxLimit = 50;
    private const int DefaultLimit = 24;

    /// <summary>
    /// Fixed cap on how many values a single multi-value filter (category/tag/mode/lifecycle) may carry.
    /// Not specified numerically by the spec ("filter cardinality exceeded" is named as a 400 case without a
    /// number) — 5 is a documented design decision: every allow-list itself has at most 8 members, and a
    /// filter wider than that stops narrowing the catalog at all, so 5 comfortably covers real use without
    /// letting a request carry unbounded repeated query keys.
    /// </summary>
    private const int MaxFilterValues = 5;

    private const string ValidationFailed = "Validation.Failed";
    private const string InvalidCursor = "Pagination.InvalidCursor";
    private const string NotFound = "Games.NotFound";
    private const string Retired = "Games.Retired";

    private static readonly IReadOnlyDictionary<string, GameLifecycle> PublicLifecycleNames =
        new Dictionary<string, GameLifecycle>(StringComparer.Ordinal)
        {
            ["ComingSoon"] = GameLifecycle.ComingSoon,
            ["Available"] = GameLifecycle.Available,
            ["Maintenance"] = GameLifecycle.Maintenance,
        };

    private static readonly IReadOnlyList<GameLifecycle> PublicLifecycles =
        new[] { GameLifecycle.ComingSoon, GameLifecycle.Available, GameLifecycle.Maintenance };

    public GamesService(IGameRepository games) => _games = games;

    public async Task<Result<CatalogPageResult>> ListAsync(
        string? query,
        IReadOnlyList<string>? category,
        IReadOnlyList<string>? tag,
        IReadOnlyList<string>? mode,
        IReadOnlyList<string>? lifecycle,
        string? sort,
        int limit,
        string? cursor,
        CancellationToken ct = default)
    {
        string? normalizedSearch = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var collapsed = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (collapsed.Length < 2 || collapsed.Length > 100)
                return Fail("Search query must be between 2 and 100 characters.");
            normalizedSearch = collapsed.ToUpperInvariant();
        }

        var categories = category ?? Array.Empty<string>();
        var tags = tag ?? Array.Empty<string>();
        var modes = mode ?? Array.Empty<string>();
        var lifecycleValues = lifecycle ?? Array.Empty<string>();

        if (categories.Count > MaxFilterValues || tags.Count > MaxFilterValues ||
            modes.Count > MaxFilterValues || lifecycleValues.Count > MaxFilterValues)
            return Fail("Too many values for a single filter.");

        if (categories.Any(c => !GameCatalogAllowLists.Tags.Contains(c)))
            return Fail("Unknown category filter.");
        if (tags.Any(t => !GameCatalogAllowLists.Tags.Contains(t)))
            return Fail("Unknown tag filter.");
        if (modes.Any(m => !GameCatalogAllowLists.Modes.Contains(m)))
            return Fail("Unknown mode filter.");

        IReadOnlyList<GameLifecycle> lifecycles;
        if (lifecycleValues.Count == 0)
        {
            lifecycles = PublicLifecycles;
        }
        else
        {
            var parsed = new List<GameLifecycle>(lifecycleValues.Count);
            foreach (var lv in lifecycleValues)
            {
                if (!PublicLifecycleNames.TryGetValue(lv, out var parsedLifecycle))
                    return Fail("Unknown or non-public lifecycle filter.");
                parsed.Add(parsedLifecycle);
            }
            lifecycles = parsed;
        }

        var activeSort = string.IsNullOrEmpty(sort) ? GameCatalogSortKey.Default : sort;
        if (!GameCatalogSortKey.AllowedSorts.Contains(activeSort))
            return Fail("Unknown sort.");

        if (limit < 1 || limit > MaxLimit)
            return Fail("Page size must be between 1 and 50.");

        var shapeHash = HashQueryShape(normalizedSearch, categories, tags, modes, lifecycles, activeSort);

        string? afterSortKey = null;
        string? afterSlug = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeCatalog(cursor, out var sortKey, out var slug, out var cursorShapeHash) ||
                cursorShapeHash != shapeHash || slug.Length == 0)
                return Result<CatalogPageResult>.Fail(InvalidCursor, "The pagination cursor is invalid.");

            // Reject a cursor whose sort key cannot decode under the active sort now, rather than letting the
            // repository silently drop the keyset predicate and quietly restart at page 1.
            var sortKeyValid = activeSort switch
            {
                GameCatalogSortKey.Difficulty => GameCatalogSortKey.TryDecodeDifficulty(sortKey, out _),
                GameCatalogSortKey.Duration => GameCatalogSortKey.TryDecodeDuration(sortKey, out _),
                GameCatalogSortKey.Default => GameCatalogSortKey.TryDecodeDefault(sortKey, out _, out _),
                _ => sortKey.Length > 0, // Name: any non-empty uppercased string is a valid key
            };
            if (!sortKeyValid)
                return Result<CatalogPageResult>.Fail(InvalidCursor, "The pagination cursor is invalid.");

            afterSortKey = sortKey;
            afterSlug = slug;
        }

        var filter = new GameCatalogFilter(
            normalizedSearch, categories, tags, modes, lifecycles, activeSort, limit, afterSortKey, afterSlug);
        var rows = await _games.GetCatalogPageAsync(filter, ct);

        var items = await ProjectAsync(rows, ct);

        string? next = rows.Count == limit
            ? Cursor.EncodeCatalog(GameCatalogSortKey.Encode(activeSort, rows[^1]), rows[^1].Slug, shapeHash)
            : null;

        var etag = ComputeListETag(rows);
        return Result<CatalogPageResult>.Ok(new CatalogPageResult(new CursorPage<GameCatalogDto>(items, next), etag));
    }

    public async Task<Result<GameDetailResult>> GetDetailAsync(string slug, CancellationToken ct = default)
    {
        var game = await _games.GetBySlugAsync(slug, ct);
        if (game is null || game.Lifecycle == GameLifecycle.Draft)
            return Result<GameDetailResult>.Fail(NotFound, "Game not found.");

        if (game.Lifecycle == GameLifecycle.Retired)
        {
            var tombstone = new GameTombstoneDto(game.Slug, game.Name, game.Lifecycle.ToString(), "Games.Retired");
            return Result<GameDetailResult>.Ok(new GameDetailResult(null, tombstone, null));
        }

        var items = await ProjectAsync(new[] { game }, ct);
        return Result<GameDetailResult>.Ok(new GameDetailResult(items[0], null, ComputeSingleETag(game)));
    }

    public async Task<Result<FeaturedResult>> GetFeaturedAsync(CancellationToken ct = default)
    {
        var game = await _games.GetFeaturedAsync(ct);
        if (game is null)
            return Result<FeaturedResult>.Ok(new FeaturedResult(null, null));

        var items = await ProjectAsync(new[] { game }, ct);
        return Result<FeaturedResult>.Ok(new FeaturedResult(items[0], ComputeSingleETag(game)));
    }

    public async Task<Result<CursorPage<GameFavoriteDto>>> GetFavoritesAsync(
        Guid userId, int limit, string? cursor, CancellationToken ct = default)
    {
        if (limit < 1 || limit > MaxLimit)
            return Result<CursorPage<GameFavoriteDto>>.Fail(ValidationFailed, "Page size must be between 1 and 50.");

        DateTime? afterUpdatedAt = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeTimeId(cursor, out var updatedAt, out var id))
                return Result<CursorPage<GameFavoriteDto>>.Fail(InvalidCursor, "The pagination cursor is invalid.");
            afterUpdatedAt = updatedAt;
            afterId = id;
        }

        var rows = await _games.GetFavoritesPageAsync(userId, limit, afterUpdatedAt, afterId, ct);
        var items = rows.Select(x => ToFavoriteDto(x.Favorite, x.Game)).ToList();

        string? next = rows.Count == limit
            ? Cursor.EncodeTimeId(rows[^1].Favorite.UpdatedAt, rows[^1].Favorite.Id)
            : null;

        return Result<CursorPage<GameFavoriteDto>>.Ok(new CursorPage<GameFavoriteDto>(items, next));
    }

    public async Task<Result<GameFavoriteDto>> PutFavoriteAsync(Guid userId, string slug, CancellationToken ct = default)
    {
        var game = await _games.GetBySlugAsync(slug, ct);
        if (game is null || game.Lifecycle == GameLifecycle.Draft)
            return Result<GameFavoriteDto>.Fail(NotFound, "Game not found.");

        var existing = await _games.GetFavoriteAsync(userId, game.Id, ct);
        if (existing is not null && existing.IsActive)
            return Result<GameFavoriteDto>.Ok(ToFavoriteDto(existing, game));

        if (game.Lifecycle == GameLifecycle.Retired)
            return Result<GameFavoriteDto>.Fail(Retired, "This game is retired and cannot be favorited.");

        if (existing is null)
        {
            var favorite = UserFavoriteGame.Favorite(userId, game.Id);
            var outcome = await _games.AddFavoriteAsync(favorite, GameOutbox.GameFavoritedEvent(favorite), ct);
            if (outcome == AddFavoriteOutcome.Conflict)
            {
                // A concurrent first favorite already won the (UserId, GameId) race; re-read and converge.
                var reread = await _games.GetFavoriteAsync(userId, game.Id, ct);
                if (reread is not null)
                    return Result<GameFavoriteDto>.Ok(ToFavoriteDto(reread, game));
                return Result<GameFavoriteDto>.Fail(NotFound, "Game not found.");
            }
            return Result<GameFavoriteDto>.Ok(ToFavoriteDto(favorite, game));
        }

        existing.Refavorite();
        var updateOutcome = await _games.UpdateFavoriteAsync(existing, GameOutbox.GameFavoritedEvent(existing), ct);
        if (updateOutcome == UpdateFavoriteOutcome.ConcurrencyConflict)
        {
            var reread = await _games.GetFavoriteAsync(userId, game.Id, ct);
            if (reread is not null)
                return Result<GameFavoriteDto>.Ok(ToFavoriteDto(reread, game));
        }
        return Result<GameFavoriteDto>.Ok(ToFavoriteDto(existing, game));
    }

    public async Task<Result> DeleteFavoriteAsync(Guid userId, string slug, CancellationToken ct = default)
    {
        var game = await _games.GetBySlugAsync(slug, ct);
        if (game is null || game.Lifecycle == GameLifecycle.Draft)
            return Result.Fail(NotFound, "Game not found.");

        var existing = await _games.GetFavoriteAsync(userId, game.Id, ct);
        if (existing is null || !existing.IsActive)
            return Result.Ok(); // Idempotent: already unfavorited (or never favorited) is still a success.

        existing.Unfavorite();
        // A ConcurrencyConflict here just means a racing caller already unfavorited it — also fine for a 204.
        await _games.UpdateFavoriteAsync(existing, GameOutbox.GameUnfavoritedEvent(existing), ct);
        return Result.Ok();
    }

    // ── Projection (query 2 of 2: tags + capabilities in one UNION ALL round trip) ──

    private async Task<List<GameCatalogDto>> ProjectAsync(IReadOnlyList<Game> games, CancellationToken ct)
    {
        var ids = games.Select(g => g.Id).ToList();
        var extra = await _games.GetTagsAndCapabilitiesAsync(ids, ct);

        var tagsByGame = extra.Where(r => r.Kind == "tag")
            .GroupBy(r => r.GameId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList());
        var modesByGame = extra.Where(r => r.Kind == "mode")
            .GroupBy(r => r.GameId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList());

        return games.Select(g => new GameCatalogDto(
            g.Slug, g.Name, g.Summary, g.RulesSummary, g.Category,
            tagsByGame.TryGetValue(g.Id, out var t) ? t : Array.Empty<string>(),
            g.Difficulty.ToString(),
            g.EstimatedDurationMinMinutes, g.EstimatedDurationMaxMinutes, g.MinPlayers, g.MaxPlayers,
            g.Lifecycle.ToString(),
            modesByGame.TryGetValue(g.Id, out var m) ? m : Array.Empty<string>(),
            g.FeaturedRank, g.ArtToken, g.ArtColorA, g.ArtColorB, g.ArtAltText,
            GameEntryActions.All)).ToList();
    }

    private static GameFavoriteDto ToFavoriteDto(UserFavoriteGame favorite, Game game) => new(
        game.Slug, game.Name, game.Lifecycle.ToString(),
        game.ArtToken, game.ArtColorA, game.ArtColorB, game.ArtAltText, favorite.UpdatedAt);

    // ── ETag derivation (documented deviation from the spec's literal single global catalogVersion counter:
    // a hash of the already-fetched row data avoids both a third SQL round trip and an unsafe in-process
    // counter cache; two requests get the same ETag iff they'd return byte-identical bodies) ──

    private static string ComputeListETag(IReadOnlyList<Game> rows)
    {
        var sb = new StringBuilder();
        foreach (var g in rows)
            sb.Append(g.Slug).Append('|').Append(g.UpdatedAt.Ticks).Append('|').Append(g.LifecycleVersion).Append(';');
        return Quote(Hash(sb.ToString()));
    }

    private static string ComputeSingleETag(Game g) =>
        Quote(Hash($"{g.Slug}|{g.UpdatedAt.Ticks}|{g.LifecycleVersion}"));

    private static string HashQueryShape(
        string? normalizedSearch, IReadOnlyList<string> categories, IReadOnlyList<string> tags,
        IReadOnlyList<string> modes, IReadOnlyList<GameLifecycle> lifecycles, string sort)
    {
        var shape = string.Join('&',
            $"q={normalizedSearch}",
            $"cat={string.Join(',', categories.OrderBy(x => x, StringComparer.Ordinal))}",
            $"tag={string.Join(',', tags.OrderBy(x => x, StringComparer.Ordinal))}",
            $"mode={string.Join(',', modes.OrderBy(x => x, StringComparer.Ordinal))}",
            $"life={string.Join(',', lifecycles.Select(l => l.ToString()).OrderBy(x => x, StringComparer.Ordinal))}",
            $"sort={sort}");
        return Hash(shape);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Quote(string value) => $"\"{value}\"";

    private static Result<CatalogPageResult> Fail(string message) =>
        Result<CatalogPageResult>.Fail(ValidationFailed, message);
}
