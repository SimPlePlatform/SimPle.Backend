using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// Metadata is what the registry keys on and what the Module 4 catalog is reconciled against, so a definition
/// that declares an impossible shape (zero players, a nine-seat table, an unknown mode) must be impossible to
/// construct — not merely rejected later at startup.
/// </summary>
public sealed class GameDefinitionMetadataTests
{
    private static GameDefinitionMetadata Create(
        int engineVersion = 1,
        int stateSchemaVersion = 1,
        int minPlayers = 2,
        int maxPlayers = 4,
        IEnumerable<string>? modes = null,
        bool supportsAi = false,
        bool supportsRanked = false) =>
        GameDefinitionMetadata.Create(
            slug: "hidden-token-draft",
            engineVersion: engineVersion,
            stateSchemaVersion: stateSchemaVersion,
            minPlayers: minPlayers,
            maxPlayers: maxPlayers,
            supportedModes: modes ?? ["multiplayer"],
            supportsAi: supportsAi,
            supportsRanked: supportsRanked);

    [Fact]
    public void Create_WithAValidShape_ExposesTheRegistryKey()
    {
        var metadata = Create();

        metadata.Slug.Should().Be("hidden-token-draft");
        metadata.EngineVersion.Should().Be(1);
        metadata.ToString().Should().Be("hidden-token-draft@v1", "the (slug, engineVersion) pair is the registry key");
        metadata.SupportsDeterministicReplay.Should().BeTrue("determinism is the default expectation, not an opt-in");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositiveEngineVersion_Throws(int engineVersion)
    {
        var create = () => Create(engineVersion: engineVersion);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositiveStateSchemaVersion_Throws(int stateSchemaVersion)
    {
        var create = () => Create(stateSchemaVersion: stateSchemaVersion);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithFewerThanOnePlayer_Throws()
    {
        var create = () => Create(minPlayers: 0, maxPlayers: 4);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithMoreThanEightPlayers_Throws()
    {
        // Eight is a hard host cap, not a suggestion: it bounds every per-seat projection the host must build.
        var create = () => Create(minPlayers: 2, maxPlayers: 9);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithMinPlayersAboveMaxPlayers_Throws()
    {
        var create = () => Create(minPlayers: 4, maxPlayers: 2);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithNoModes_Throws()
    {
        var create = () => Create(modes: []);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithAModeOutsideTheCatalogAllowList_Throws()
    {
        // The engine and the Module 4 catalog row are compared mode-for-mode by the compatibility validator, so
        // an engine inventing its own vocabulary would fail that comparison in a confusing way. Fail here first.
        var create = () => Create(modes: ["battle-royale"]);

        create.Should().Throw<ArgumentException>().WithMessage("*allow-list*");
    }

    [Fact]
    public void Create_DeclaringRankedCapabilityWithoutTheRankedMode_Throws()
    {
        var create = () => Create(modes: ["multiplayer"], supportsRanked: true);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_DeclaringTheAiModeWithoutTheAiCapability_Throws()
    {
        var create = () => Create(modes: ["multiplayer", "ai"], supportsAi: false);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithRankedModeAndCapability_Succeeds()
    {
        var metadata = Create(modes: ["multiplayer", "ranked"], supportsRanked: true);

        metadata.SupportedModes.Should().BeEquivalentTo(["multiplayer", "ranked"]);
        metadata.SupportsRanked.Should().BeTrue();
    }
}
