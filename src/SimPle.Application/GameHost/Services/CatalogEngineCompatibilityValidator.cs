using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>The one <see cref="ICatalogEngineCompatibilityValidator"/> implementation.</summary>
public sealed class CatalogEngineCompatibilityValidator : ICatalogEngineCompatibilityValidator
{
    public IReadOnlyList<CatalogCompatibilityViolation> Validate(
        IEnumerable<GameDefinitionMetadata> registeredDefinitions,
        IEnumerable<CatalogGameSnapshot> catalogSnapshots)
    {
        ArgumentNullException.ThrowIfNull(registeredDefinitions);
        ArgumentNullException.ThrowIfNull(catalogSnapshots);

        var catalogBySlug = catalogSnapshots.ToDictionary(snapshot => snapshot.Slug, StringComparer.Ordinal);
        var violations = new List<CatalogCompatibilityViolation>();

        foreach (var definition in registeredDefinitions)
        {
            if (!catalogBySlug.TryGetValue(definition.Slug, out var catalog))
                continue;

            if (definition.MinPlayers > catalog.MinPlayers || definition.MaxPlayers < catalog.MaxPlayers)
            {
                violations.Add(CatalogCompatibilityViolation.Create(
                    definition.Slug,
                    $"Engine supports {definition.MinPlayers}-{definition.MaxPlayers} players but the catalog " +
                    $"advertises {catalog.MinPlayers}-{catalog.MaxPlayers}."));
            }

            var missingModes = catalog.Modes.Except(definition.SupportedModes, StringComparer.Ordinal).ToList();
            if (missingModes.Count > 0)
            {
                violations.Add(CatalogCompatibilityViolation.Create(
                    definition.Slug,
                    $"Catalog advertises mode(s) [{string.Join(", ", missingModes)}] the engine does not support."));
            }
        }

        return violations;
    }
}
