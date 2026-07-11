using FluentAssertions;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The startup drift check (D1): a registered engine and its Module 4 catalog row are compared independently
/// via <see cref="CatalogGameSnapshot"/>, not M4's real entity, so the zero/matching/mismatched matrix here
/// stays fixture-only.
/// </summary>
public sealed class CatalogEngineCompatibilityValidatorTests
{
    private readonly CatalogEngineCompatibilityValidator _validator = new();

    private static GameDefinitionMetadata Metadata(
        string slug, int minPlayers, int maxPlayers, params string[] modes) => GameDefinitionMetadata.Create(
        slug: slug, engineVersion: 1, stateSchemaVersion: 1, minPlayers: minPlayers, maxPlayers: maxPlayers, supportedModes: modes);

    private static CatalogGameSnapshot Catalog(string slug, int minPlayers, int maxPlayers, params string[] modes) =>
        CatalogGameSnapshot.Create(slug, minPlayers, maxPlayers, modes);

    [Fact]
    public void Validate_NoCatalogRowMatchesTheEngine_ProducesNoViolation()
    {
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 2, 4, "multiplayer")],
            []);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_NoEngineMatchesTheCatalogRow_ProducesNoViolation()
    {
        var violations = _validator.Validate(
            [],
            [Catalog("some-catalog-only-game", 2, 4, "multiplayer")]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MatchedPairWithCompatibleBoundsAndModes_ProducesNoViolation()
    {
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 2, 4, "multiplayer", "ranked")],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer")]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EngineSupportsFewerPlayersThanCatalogAdvertises_ProducesAViolation()
    {
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 2, 3, "multiplayer")],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer")]);

        violations.Should().ContainSingle(v => v.Slug == "hidden-token-draft");
    }

    [Fact]
    public void Validate_EngineRequiresMorePlayersThanCatalogMinimum_ProducesAViolation()
    {
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 3, 4, "multiplayer")],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer")]);

        violations.Should().ContainSingle(v => v.Slug == "hidden-token-draft");
    }

    [Fact]
    public void Validate_CatalogAdvertisesAModeTheEngineDoesNotSupport_ProducesAViolation()
    {
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 2, 4, "multiplayer")],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer", "cooperative")]);

        violations.Should().ContainSingle(v => v.Slug == "hidden-token-draft" && v.Reason.Contains("cooperative"));
    }

    [Fact]
    public void Validate_EngineSupportsMoreModesThanCatalogAdvertises_ProducesNoViolation()
    {
        // An engine capable of more than the catalog currently advertises is not drift — only the reverse is.
        var violations = _validator.Validate(
            [Metadata("hidden-token-draft", 2, 4, "multiplayer", "cooperative")],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer")]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MultipleRegisteredEngineVersionsForTheSameSlug_EachComparedAgainstTheSameCatalogRow()
    {
        var violations = _validator.Validate(
            [
                Metadata("hidden-token-draft", 2, 4, "multiplayer"),
                Metadata("hidden-token-draft", 1, 2, "multiplayer"),
            ],
            [Catalog("hidden-token-draft", 2, 4, "multiplayer")]);

        violations.Should().ContainSingle(v => v.Reason.Contains("1-2"));
    }
}
