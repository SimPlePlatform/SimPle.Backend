using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;

namespace SimPle.Infrastructure.Games;

public sealed record GameCatalogSeedResult(bool Success, string Message, int GamesCreated, int GamesUpdated);

/// <summary>
/// Loads the embedded catalog seed manifest, validates it against domain invariants before touching the
/// database, then upserts the catalog inside a transaction guarded by a Postgres advisory lock so concurrent
/// seeder runs converge safely. See docs/specs/module-04-game-library-discovery-spec.md "Seeder" section.
/// </summary>
public sealed class GameCatalogSeeder
{
    // Module-4-specific advisory lock key. Must never collide with another module's advisory lock —
    // no other module uses advisory locks today.
    private const long CatalogSeedAdvisoryLockKey = 44004001;

    private static readonly string ResourceName =
        typeof(GameCatalogSeeder).Assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("catalog.seed.v1.json", StringComparison.Ordinal));

    private readonly AppDbContext _db;
    private readonly ILogger<GameCatalogSeeder> _logger;

    public GameCatalogSeeder(AppDbContext db, ILogger<GameCatalogSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GameCatalogSeedResult> SeedAsync(CancellationToken ct = default)
    {
        byte[] manifestBytes;
        await using (var stream = typeof(GameCatalogSeeder).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (stream is null)
                return Fail("Embedded catalog seed manifest resource not found.");

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            manifestBytes = buffer.ToArray();
        }

        CatalogSeedManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CatalogSeedManifest>(manifestBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Manifest deserialized to null.");
        }
        catch (JsonException ex)
        {
            return Fail($"Manifest failed to parse: {ex.Message}");
        }

        var validationError = Validate(manifest);
        if (validationError is not null)
            return Fail(validationError);

        var checksum = ComputeManifestChecksum(manifestBytes);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            $"SELECT pg_advisory_xact_lock({CatalogSeedAdvisoryLockKey});", ct);

        var existingHistory = await _db.Set<CatalogSeedHistory>()
            .FirstOrDefaultAsync(h => h.ManifestVersion == manifest.ManifestVersion, ct);

        if (existingHistory is not null)
        {
            if (existingHistory.Checksum != checksum)
            {
                await transaction.RollbackAsync(ct);
                var message = $"Checksum mismatch for manifest version '{manifest.ManifestVersion}', refusing to overwrite.";
                _logger.LogError("Game catalog seed failed: {Message}", message);
                return new GameCatalogSeedResult(false, message, 0, 0);
            }

            await transaction.CommitAsync(ct);
            var noopMessage = $"Manifest version '{manifest.ManifestVersion}' already applied; no-op.";
            _logger.LogInformation("Game catalog seed no-op: {Message}", noopMessage);
            return new GameCatalogSeedResult(true, noopMessage, 0, 0);
        }

        var created = 0;
        var updated = 0;

        try
        {
            foreach (var entry in manifest.Games)
            {
                var difficulty = Enum.Parse<GameDifficulty>(entry.Difficulty);

                var existingGame = await _db.Games
                    .Include(g => g.Tags)
                    .Include(g => g.Capabilities)
                    .FirstOrDefaultAsync(g => g.Slug == entry.Slug, ct);

                if (existingGame is null)
                {
                    var game = Game.Create(
                        entry.Slug,
                        entry.Name,
                        entry.Summary,
                        entry.RulesSummary,
                        difficulty,
                        entry.EstimatedDurationMinMinutes,
                        entry.EstimatedDurationMaxMinutes,
                        entry.MinPlayers,
                        entry.MaxPlayers,
                        GameLifecycle.ComingSoon,
                        entry.FeaturedRank,
                        entry.SortOrder,
                        entry.ArtToken,
                        entry.ArtColorA,
                        entry.ArtColorB,
                        entry.ArtAltText,
                        manifest.ManifestVersion,
                        entry.Category,
                        entry.Tags,
                        entry.Modes);
                    _db.Games.Add(game);
                    created++;
                }
                else
                {
                    existingGame.ApplyManifestUpdate(
                        entry.Name,
                        entry.Summary,
                        entry.RulesSummary,
                        difficulty,
                        entry.EstimatedDurationMinMinutes,
                        entry.EstimatedDurationMaxMinutes,
                        entry.MinPlayers,
                        entry.MaxPlayers,
                        entry.FeaturedRank,
                        entry.SortOrder,
                        entry.ArtToken,
                        entry.ArtColorA,
                        entry.ArtColorB,
                        entry.ArtAltText,
                        manifest.ManifestVersion,
                        entry.Category,
                        entry.Tags,
                        entry.Modes);
                    updated++;
                }
            }

            _db.Set<CatalogSeedHistory>().Add(CatalogSeedHistory.Record(manifest.ManifestVersion, checksum));

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(ct);
            var message = $"Failed to apply manifest version '{manifest.ManifestVersion}': {ex.Message}";
            _logger.LogError(ex, "Game catalog seed failed: {Message}", message);
            return new GameCatalogSeedResult(false, message, 0, 0);
        }

        var successMessage =
            $"Applied manifest version '{manifest.ManifestVersion}': {created} created, {updated} updated.";
        _logger.LogInformation("Game catalog seed succeeded: {Message}", successMessage);
        return new GameCatalogSeedResult(true, successMessage, created, updated);
    }

    private GameCatalogSeedResult Fail(string message)
    {
        _logger.LogError("Game catalog seed failed: {Message}", message);
        return new GameCatalogSeedResult(false, message, 0, 0);
    }

    /// <summary>
    /// Hashes the manifest with canonical LF line endings. Git's core.autocrlf must never make a
    /// semantically identical checked-in manifest appear to be a different seed revision on Windows.
    /// Formatting/content changes still change the checksum and therefore remain fail-closed.
    /// </summary>
    public static string ComputeManifestChecksum(ReadOnlySpan<byte> manifestBytes)
    {
        var normalized = Encoding.UTF8.GetString(manifestBytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    /// <summary>
    /// Structural + domain-invariant validation of the whole manifest before any database access.
    /// Returns null when valid, or a message identifying the offending slug/field otherwise.
    /// </summary>
    private static string? Validate(CatalogSeedManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion))
            return "manifestVersion must not be empty.";

        if (manifest.Games is null || manifest.Games.Count == 0)
            return "games must contain at least one entry.";

        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        var featuredCount = 0;

        foreach (var entry in manifest.Games)
        {
            if (string.IsNullOrWhiteSpace(entry.Slug))
                return "A game entry is missing a slug.";
            if (!seenSlugs.Add(entry.Slug))
                return $"Duplicate slug '{entry.Slug}' in manifest.";

            if (string.IsNullOrWhiteSpace(entry.Name))
                return $"[{entry.Slug}] name must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.Summary))
                return $"[{entry.Slug}] summary must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.RulesSummary))
                return $"[{entry.Slug}] rulesSummary must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.ArtToken))
                return $"[{entry.Slug}] artToken must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.ArtColorA))
                return $"[{entry.Slug}] artColorA must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.ArtColorB))
                return $"[{entry.Slug}] artColorB must not be empty.";
            if (string.IsNullOrWhiteSpace(entry.ArtAltText))
                return $"[{entry.Slug}] artAltText must not be empty.";

            if (!Enum.TryParse<GameDifficulty>(entry.Difficulty, out _))
                return $"[{entry.Slug}] difficulty '{entry.Difficulty}' is not a valid GameDifficulty.";

            if (entry.MinPlayers < 1)
                return $"[{entry.Slug}] minPlayers must be at least 1.";
            if (entry.MinPlayers > entry.MaxPlayers)
                return $"[{entry.Slug}] minPlayers must be <= maxPlayers.";
            if (entry.EstimatedDurationMinMinutes > entry.EstimatedDurationMaxMinutes)
                return $"[{entry.Slug}] estimatedDurationMinMinutes must be <= estimatedDurationMaxMinutes.";

            if (string.IsNullOrWhiteSpace(entry.Category))
                return $"[{entry.Slug}] category must not be empty.";
            if (!GameCatalogAllowLists.Tags.Contains(entry.Category))
                return $"[{entry.Slug}] category '{entry.Category}' is not in the tag allow-list.";
            foreach (var tag in entry.Tags)
            {
                if (!GameCatalogAllowLists.Tags.Contains(tag))
                    return $"[{entry.Slug}] tag '{tag}' is not in the allow-list.";
            }
            if (entry.Tags.Contains(entry.Category))
                return $"[{entry.Slug}] tag '{entry.Category}' duplicates the category and must not be repeated.";
            if (entry.Tags.Distinct(StringComparer.Ordinal).Count() != entry.Tags.Count)
                return $"[{entry.Slug}] tags contain a duplicate value.";

            if (entry.Modes is null || entry.Modes.Count == 0)
                return $"[{entry.Slug}] modes must contain at least one entry.";
            foreach (var mode in entry.Modes)
            {
                if (!GameCatalogAllowLists.Modes.Contains(mode))
                    return $"[{entry.Slug}] mode '{mode}' is not in the allow-list.";
            }
            if (entry.Modes.Distinct(StringComparer.Ordinal).Count() != entry.Modes.Count)
                return $"[{entry.Slug}] modes contain a duplicate value.";
            if (entry.Modes.Contains("ranked") && !entry.Modes.Contains("multiplayer"))
                return $"[{entry.Slug}] mode 'ranked' requires 'multiplayer' to also be present.";

            if (entry.FeaturedRank is not null)
            {
                if (entry.FeaturedRank != 1)
                    return $"[{entry.Slug}] featuredRank must be 1 when present.";
                featuredCount++;
            }
        }

        if (featuredCount > 1)
            return "At most one game may have a non-null featuredRank.";

        return null;
    }
}
