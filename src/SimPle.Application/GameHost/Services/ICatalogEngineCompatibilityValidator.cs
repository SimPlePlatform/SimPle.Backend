using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// Compares every registered engine's metadata against its Module 4 catalog row, so a startup check catches
/// drift (an engine that supports fewer players or fewer modes than the catalog advertises) before a client
/// ever hits it as a runtime rejection.
/// </summary>
public interface ICatalogEngineCompatibilityValidator
{
    /// <summary>
    /// Checks each registered definition against the catalog snapshot with the same slug, if one exists. A
    /// catalog row with no matching registered engine, or a registered engine with no matching catalog row, is
    /// not itself a violation — Phase 1 catalog games have no Module 5 engine yet, and a newly registered
    /// engine may briefly precede its catalog row in a non-production environment. Only a <b>matched</b> pair
    /// with incompatible bounds or modes is reported.
    /// </summary>
    IReadOnlyList<CatalogCompatibilityViolation> Validate(
        IEnumerable<GameDefinitionMetadata> registeredDefinitions,
        IEnumerable<CatalogGameSnapshot> catalogSnapshots);
}
