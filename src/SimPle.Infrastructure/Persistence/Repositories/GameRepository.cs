using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Games;
using SimPle.Domain.Games;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Repositories;

public sealed class GameRepository : IGameRepository
{
    private readonly AppDbContext _db;

    public GameRepository(AppDbContext db) => _db = db;

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    // ── Catalog reads (query 1 of 2 — the game rows themselves) ────────────────

    public async Task<IReadOnlyList<Game>> GetCatalogPageAsync(GameCatalogFilter filter, CancellationToken ct = default)
    {
        var query = _db.Games.AsNoTracking().Where(g => filter.Lifecycles.Contains(g.Lifecycle));

        if (filter.Categories.Count > 0)
            query = query.Where(g => filter.Categories.Contains(g.Category));

        if (filter.Tags.Count > 0)
            query = query.Where(g => _db.GameTags.Any(t => t.GameId == g.Id && filter.Tags.Contains(t.Value)));

        if (filter.Modes.Count > 0)
            query = query.Where(g => _db.GameModeCapabilities.Any(c => c.GameId == g.Id && filter.Modes.Contains(c.Mode)));

        if (filter.NormalizedSearch is string search)
        {
            query = query.Where(g =>
                g.Name.ToUpper().Contains(search) ||
                g.Summary.ToUpper().Contains(search) ||
                g.Category.ToUpper().Contains(search) ||
                _db.GameTags.Any(t => t.GameId == g.Id && t.Value.ToUpper().Contains(search)));
        }

        switch (filter.Sort)
        {
            case GameCatalogSortKey.Name:
            {
                if (filter.AfterSortKey is string afterName && filter.AfterSlug is string afterSlugN)
                {
                    query = query.Where(g =>
                        g.Name.ToUpper().CompareTo(afterName) > 0 ||
                        (g.Name.ToUpper() == afterName && g.Slug.CompareTo(afterSlugN) > 0));
                }
                return await query.OrderBy(g => g.Name.ToUpper()).ThenBy(g => g.Slug)
                    .Take(filter.Limit).ToListAsync(ct);
            }
            case GameCatalogSortKey.Difficulty:
            {
                if (filter.AfterSortKey is string afterDiffStr && filter.AfterSlug is string afterSlugD &&
                    GameCatalogSortKey.TryDecodeDifficulty(afterDiffStr, out var afterDiff))
                {
                    query = query.Where(g =>
                        (g.Difficulty == GameDifficulty.Easy ? 0 : g.Difficulty == GameDifficulty.Hard ? 2 : 1) > afterDiff ||
                        ((g.Difficulty == GameDifficulty.Easy ? 0 : g.Difficulty == GameDifficulty.Hard ? 2 : 1) == afterDiff &&
                            g.Slug.CompareTo(afterSlugD) > 0));
                }
                return await query
                    .OrderBy(g => g.Difficulty == GameDifficulty.Easy ? 0 : g.Difficulty == GameDifficulty.Hard ? 2 : 1)
                    .ThenBy(g => g.Slug)
                    .Take(filter.Limit).ToListAsync(ct);
            }
            case GameCatalogSortKey.Duration:
            {
                if (filter.AfterSortKey is string afterDurStr && filter.AfterSlug is string afterSlugU &&
                    GameCatalogSortKey.TryDecodeDuration(afterDurStr, out var afterDur))
                {
                    query = query.Where(g =>
                        g.EstimatedDurationMinMinutes > afterDur ||
                        (g.EstimatedDurationMinMinutes == afterDur && g.Slug.CompareTo(afterSlugU) > 0));
                }
                return await query.OrderBy(g => g.EstimatedDurationMinMinutes).ThenBy(g => g.Slug)
                    .Take(filter.Limit).ToListAsync(ct);
            }
            default: // GameCatalogSortKey.Default: (FeaturedRank NULLS LAST, SortOrder, Slug)
            {
                if (filter.AfterSortKey is string afterKey && filter.AfterSlug is string afterSlugDef &&
                    GameCatalogSortKey.TryDecodeDefault(afterKey, out var afterRank, out var afterSortOrder))
                {
                    if (afterRank == GameCatalogSortKey.NoFeaturedRank)
                    {
                        // Prior row was already in the null tail — only later null rows remain.
                        query = query.Where(g =>
                            g.FeaturedRank == null &&
                            (g.SortOrder > afterSortOrder || (g.SortOrder == afterSortOrder && g.Slug.CompareTo(afterSlugDef) > 0)));
                    }
                    else
                    {
                        // Prior row had a real rank: later real ranks, ties on (SortOrder, Slug), or the null tail.
                        query = query.Where(g =>
                            g.FeaturedRank == null ||
                            g.FeaturedRank > afterRank ||
                            (g.FeaturedRank == afterRank &&
                                (g.SortOrder > afterSortOrder || (g.SortOrder == afterSortOrder && g.Slug.CompareTo(afterSlugDef) > 0))));
                    }
                }
                return await query.OrderBy(g => g.FeaturedRank).ThenBy(g => g.SortOrder).ThenBy(g => g.Slug)
                    .Take(filter.Limit).ToListAsync(ct);
            }
        }
    }

    // ── Catalog reads (query 2 of 2 — tags + capabilities, one UNION ALL round trip) ──

    public async Task<IReadOnlyList<(Guid GameId, string Kind, string Value)>> GetTagsAndCapabilitiesAsync(
        IReadOnlyList<Guid> gameIds, CancellationToken ct = default)
    {
        if (gameIds.Count == 0) return Array.Empty<(Guid, string, string)>();

        var tagRows = _db.GameTags
            .Where(t => gameIds.Contains(t.GameId))
            .Select(t => new { t.GameId, Kind = "tag", Value = t.Value });

        var capRows = _db.GameModeCapabilities
            .Where(c => gameIds.Contains(c.GameId))
            .Select(c => new { c.GameId, Kind = "mode", Value = c.Mode });

        // EF translates Concat over two queries against the same context into a single UNION ALL statement.
        var rows = await tagRows.Concat(capRows).ToListAsync(ct);
        return rows.Select(r => (r.GameId, r.Kind, r.Value)).ToList();
    }

    public Task<Game?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Slug == slug, ct);

    public Task<Game?> GetFeaturedAsync(CancellationToken ct = default) =>
        _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.FeaturedRank == 1, ct);

    // ── Favorites ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<(UserFavoriteGame Favorite, Game Game)>> GetFavoritesPageAsync(
        Guid userId, int limit, DateTime? afterFavoritedAt, Guid? afterId, CancellationToken ct = default)
    {
        var query = _db.UserFavoriteGames.AsNoTracking().Where(f => f.UserId == userId && f.IsActive);

        if (afterFavoritedAt is DateTime afa && afterId is Guid ai)
        {
            query = query.Where(f => f.UpdatedAt < afa || (f.UpdatedAt == afa && f.Id.CompareTo(ai) < 0));
        }

        var rows = await query
            .OrderByDescending(f => f.UpdatedAt).ThenByDescending(f => f.Id)
            .Take(limit)
            .Join(_db.Games, f => f.GameId, g => g.Id, (f, g) => new { f, g })
            .OrderByDescending(x => x.f.UpdatedAt).ThenByDescending(x => x.f.Id)
            .ToListAsync(ct);

        return rows.Select(x => (x.f, x.g)).ToList();
    }

    public Task<UserFavoriteGame?> GetFavoriteAsync(Guid userId, Guid gameId, CancellationToken ct = default) =>
        _db.UserFavoriteGames.FirstOrDefaultAsync(f => f.UserId == userId && f.GameId == gameId, ct);

    public async Task<AddFavoriteOutcome> AddFavoriteAsync(
        UserFavoriteGame favorite, OutboxMessage evt, CancellationToken ct = default)
    {
        try
        {
            await _db.UserFavoriteGames.AddAsync(favorite, ct);
            await _db.OutboxMessages.AddAsync(evt, ct);
            await PostgresRetry.SaveChangesAsync(_db, ct);
            return AddFavoriteOutcome.Added;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            Detach(favorite, evt);
            return AddFavoriteOutcome.Conflict;
        }
    }

    public async Task<UpdateFavoriteOutcome> UpdateFavoriteAsync(
        UserFavoriteGame favorite, OutboxMessage evt, CancellationToken ct = default)
    {
        try
        {
            _db.UserFavoriteGames.Update(favorite);
            await _db.OutboxMessages.AddAsync(evt, ct);
            await PostgresRetry.SaveChangesAsync(_db, ct);
            return UpdateFavoriteOutcome.Updated;
        }
        catch (DbUpdateException)
        {
            // Outbox uniqueness rejected a retried transition, or a racing writer won: re-read and converge.
            Detach(favorite, evt);
            return UpdateFavoriteOutcome.ConcurrencyConflict;
        }
    }

    private void Detach(UserFavoriteGame f, OutboxMessage evt)
    {
        _db.Entry(f).State = EntityState.Detached;
        _db.Entry(evt).State = EntityState.Detached;
    }
}
