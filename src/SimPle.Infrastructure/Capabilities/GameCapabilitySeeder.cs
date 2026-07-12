using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimPle.Domain.Capabilities;
using SimPle.Infrastructure.Persistence;

namespace SimPle.Infrastructure.Capabilities;

public sealed record CapabilitySeedResult(bool Success, string Message, int ProfilesCreated, int ProfilesUpdated);

/// <summary>
/// Loads the embedded capability manifest, validates it against domain invariants <em>and</em> against Module 4's
/// live catalog before touching the database, then upserts the profiles inside a transaction guarded by a Postgres
/// advisory lock so concurrent seeder runs converge safely.
///
/// Mirrors <see cref="SimPle.Infrastructure.Games.GameCatalogSeeder"/> deliberately — same embedded-resource +
/// checksum + advisory-lock + fail-closed-on-mismatch shape — with two differences that matter:
///
/// 1. A <b>distinct advisory-lock key</b> (44004001 belongs to the catalog seeder). Sharing it would serialize two
///    unrelated seeders against each other for no reason, and would deadlock if either ever took the other's lock.
/// 2. A <b>cross-catalog check</b>: every profile must be a subset of the M4 catalog row for the same slug. A
///    profile that permits seat counts or modes the catalog does not would let a lobby be created that M5's engine
///    cannot host, so the seeder refuses to write it rather than deferring the failure to a player's Start click.
/// </summary>
public sealed class GameCapabilitySeeder
{
    /// <summary>
    /// Module-6-specific advisory lock key. Must never collide with another module's — the Module 4 catalog seeder
    /// holds 44004001.
    /// </summary>
    private const long CapabilitySeedAdvisoryLockKey = 44006001;

    private static readonly string ResourceName =
        typeof(GameCapabilitySeeder).Assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("capability.seed.v1.json", StringComparison.Ordinal));

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameCapabilitySeeder> _logger;

    public GameCapabilitySeeder(AppDbContext db, TimeProvider timeProvider, ILogger<GameCapabilitySeeder> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CapabilitySeedResult> SeedAsync(CancellationToken ct = default)
    {
        byte[] manifestBytes;
        await using (var stream = typeof(GameCapabilitySeeder).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (stream is null)
                return Fail("Embedded capability seed manifest resource not found.");

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            manifestBytes = buffer.ToArray();
        }

        CapabilitySeedManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CapabilitySeedManifest>(manifestBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Manifest deserialized to null.");
        }
        catch (JsonException ex)
        {
            return Fail($"Manifest failed to parse: {ex.Message}");
        }

        var structuralError = ValidateStructure(manifest);
        if (structuralError is not null)
            return Fail(structuralError);

        var checksum = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            $"SELECT pg_advisory_xact_lock({CapabilitySeedAdvisoryLockKey});", ct);

        var existingHistory = await _db.CapabilitySeedHistory
            .FirstOrDefaultAsync(h => h.ManifestVersion == manifest.ManifestVersion, ct);

        if (existingHistory is not null)
        {
            if (existingHistory.Checksum != checksum)
            {
                await transaction.RollbackAsync(ct);
                var message =
                    $"Checksum mismatch for capability manifest version '{manifest.ManifestVersion}', refusing to overwrite.";
                _logger.LogError("Capability seed failed: {Message}", message);
                return new CapabilitySeedResult(false, message, 0, 0);
            }

            await transaction.CommitAsync(ct);
            var noopMessage = $"Capability manifest version '{manifest.ManifestVersion}' already applied; no-op.";
            _logger.LogInformation("Capability seed no-op: {Message}", noopMessage);
            return new CapabilitySeedResult(true, noopMessage, 0, 0);
        }

        // Cross-check every profile against the live M4 catalog before writing anything. Done inside the
        // transaction so a concurrent catalog change cannot slip between the check and the write.
        var catalogError = await ValidateAgainstCatalogAsync(manifest, ct);
        if (catalogError is not null)
        {
            await transaction.RollbackAsync(ct);
            return Fail(catalogError);
        }

        var created = 0;
        var updated = 0;
        var appliedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            foreach (var entry in manifest.Profiles)
            {
                var existing = await _db.GameCapabilityProfiles.FirstOrDefaultAsync(
                    p => p.GameSlug == entry.GameSlug && p.CapabilityVersion == entry.CapabilityVersion, ct);

                if (existing is null)
                {
                    _db.GameCapabilityProfiles.Add(GameCapabilityProfile.Create(
                        entry.GameSlug,
                        entry.CapabilityVersion,
                        entry.MinPlayers,
                        entry.MaxPlayers,
                        entry.AllowedModes,
                        entry.TimeControls,
                        entry.TieBreakRules,
                        entry.SpectatorPolicies,
                        entry.RatedEligible,
                        entry.AiFillEligible,
                        manifest.ManifestVersion));
                    created++;
                }
                else
                {
                    existing.ApplyManifestUpdate(
                        entry.MinPlayers,
                        entry.MaxPlayers,
                        entry.AllowedModes,
                        entry.TimeControls,
                        entry.TieBreakRules,
                        entry.SpectatorPolicies,
                        entry.RatedEligible,
                        entry.AiFillEligible,
                        entry.IsActive,
                        manifest.ManifestVersion);
                    updated++;
                }
            }

            _db.CapabilitySeedHistory.Add(
                CapabilitySeedHistory.Record(manifest.ManifestVersion, checksum, appliedAtUtc));

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(ct);
            var message = $"Failed to apply capability manifest version '{manifest.ManifestVersion}': {ex.Message}";
            _logger.LogError(ex, "Capability seed failed: {Message}", message);
            return new CapabilitySeedResult(false, message, 0, 0);
        }
        catch (ArgumentException ex)
        {
            // A domain invariant the structural validator did not cover. Fail closed rather than write a profile
            // the domain would reject on read.
            await transaction.RollbackAsync(ct);
            var message = $"Capability manifest version '{manifest.ManifestVersion}' violates a domain invariant: {ex.Message}";
            _logger.LogError(ex, "Capability seed failed: {Message}", message);
            return new CapabilitySeedResult(false, message, 0, 0);
        }

        var successMessage =
            $"Applied capability manifest version '{manifest.ManifestVersion}': {created} created, {updated} updated.";
        _logger.LogInformation("Capability seed succeeded: {Message}", successMessage);
        return new CapabilitySeedResult(true, successMessage, created, updated);
    }

    private CapabilitySeedResult Fail(string message)
    {
        _logger.LogError("Capability seed failed: {Message}", message);
        return new CapabilitySeedResult(false, message, 0, 0);
    }

    /// <summary>
    /// Structural validation of the whole manifest before any database access. Domain-invariant validation
    /// (allow-lists, rated/AI mode implications) is enforced by <see cref="GameCapabilityProfile.Create"/> itself,
    /// so it is deliberately not duplicated here — only the constraints the domain cannot see are checked.
    /// Returns null when valid.
    /// </summary>
    private static string? ValidateStructure(CapabilitySeedManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion))
            return "manifestVersion must not be empty.";

        if (manifest.Profiles is null || manifest.Profiles.Count == 0)
            return "profiles must contain at least one entry.";

        var seenPins = new HashSet<string>(StringComparer.Ordinal);
        var activeBySlug = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in manifest.Profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.GameSlug))
                return "A profile entry is missing a gameSlug.";

            var pin = $"{entry.GameSlug}@{entry.CapabilityVersion}";
            if (!seenPins.Add(pin))
                return $"Duplicate pin '{pin}' in manifest.";

            // At most one active profile per game — the same rule the partial unique index enforces. Catching it
            // here turns a confusing 23505 at write time into a clear manifest error.
            if (entry.IsActive && !activeBySlug.Add(entry.GameSlug))
                return $"[{entry.GameSlug}] has more than one active capability profile in the manifest.";
        }

        return null;
    }

    /// <summary>
    /// Every profile must name a real catalog game and be a subset of it. Uses the domain's own
    /// <see cref="GameCapabilityProfile.ContradictsCatalog"/> so the seeder and the runtime command path can never
    /// disagree about what "contradicts the catalog" means.
    /// </summary>
    private async Task<string?> ValidateAgainstCatalogAsync(CapabilitySeedManifest manifest, CancellationToken ct)
    {
        var slugs = manifest.Profiles.Select(p => p.GameSlug).Distinct(StringComparer.Ordinal).ToList();

        var games = await _db.Games
            .Where(g => slugs.Contains(g.Slug))
            .Include(g => g.Capabilities)
            .ToListAsync(ct);

        var bySlug = games.ToDictionary(g => g.Slug, StringComparer.Ordinal);

        foreach (var entry in manifest.Profiles)
        {
            if (!bySlug.TryGetValue(entry.GameSlug, out var game))
            {
                return $"[{entry.GameSlug}] names a game that is not in the Module 4 catalog. " +
                       "Seed the game catalog first (--seed-game-catalog).";
            }

            GameCapabilityProfile candidate;
            try
            {
                candidate = GameCapabilityProfile.Create(
                    entry.GameSlug,
                    entry.CapabilityVersion,
                    entry.MinPlayers,
                    entry.MaxPlayers,
                    entry.AllowedModes,
                    entry.TimeControls,
                    entry.TieBreakRules,
                    entry.SpectatorPolicies,
                    entry.RatedEligible,
                    entry.AiFillEligible,
                    manifest.ManifestVersion);
            }
            catch (ArgumentException ex)
            {
                return $"[{entry.GameSlug}@{entry.CapabilityVersion}] {ex.Message}";
            }

            var drift = candidate.ContradictsCatalog(
                game.MinPlayers,
                game.MaxPlayers,
                game.Capabilities.Select(c => c.Mode));

            if (!drift.Allowed)
                return $"[{entry.GameSlug}@{entry.CapabilityVersion}] {drift.Reason}";
        }

        return null;
    }
}
