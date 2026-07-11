using FluentAssertions;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Support;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The registry is a fixed, startup-built lookup table keyed by <c>(Slug, EngineVersion)</c>: no runtime
/// registration, no "latest version" fallback, and a duplicate key must fail startup rather than silently
/// pick a winner.
/// </summary>
public sealed class GameRegistryTests
{
    private static GameDefinitionMetadata Metadata(string slug, int engineVersion) => GameDefinitionMetadata.Create(
        slug: slug,
        engineVersion: engineVersion,
        stateSchemaVersion: 1,
        minPlayers: 2,
        maxPlayers: 4,
        supportedModes: ["multiplayer"]);

    private static FakeHostedGameDefinition Fake(string slug, int engineVersion) =>
        new(Metadata(slug, engineVersion));

    [Fact]
    public void Create_WithDistinctSlugVersionPairs_RegistersEveryDefinition()
    {
        var a = Fake("hidden-token-draft", 1);
        var b = Fake("hidden-token-draft", 2);
        var c = Fake("some-other-game", 1);

        var registry = GameRegistry.Create([a, b, c]);

        registry.RegisteredDefinitions.Should().HaveCount(3);
    }

    [Fact]
    public void Create_WithDuplicateSlugAndEngineVersion_ThrowsInvalidOperationException()
    {
        var first = Fake("hidden-token-draft", 1);
        var duplicate = Fake("hidden-token-draft", 1);

        var act = () => GameRegistry.Create([first, duplicate]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_WithSameSlugButDifferentEngineVersions_DoesNotThrow()
    {
        var v1 = Fake("hidden-token-draft", 1);
        var v2 = Fake("hidden-token-draft", 2);

        var act = () => GameRegistry.Create([v1, v2]);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryResolve_ReturnsTheExactRegisteredDefinition()
    {
        var definition = Fake("hidden-token-draft", 1);
        var registry = GameRegistry.Create([definition]);

        var resolved = registry.TryResolve("hidden-token-draft", 1, out var found);

        resolved.Should().BeTrue();
        found.Should().BeSameAs(definition);
    }

    [Fact]
    public void TryResolve_UnknownSlug_ReturnsFalse()
    {
        var registry = GameRegistry.Create([Fake("hidden-token-draft", 1)]);

        var resolved = registry.TryResolve("no-such-game", 1, out var found);

        resolved.Should().BeFalse();
        found.Should().BeNull();
    }

    [Fact]
    public void TryResolve_KnownSlugButDifferentEngineVersion_DoesNotFallBack()
    {
        var registry = GameRegistry.Create([Fake("hidden-token-draft", 1)]);

        var resolved = registry.TryResolve("hidden-token-draft", 2, out var found);

        resolved.Should().BeFalse();
        found.Should().BeNull();
    }

    [Fact]
    public void RegisteredDefinitions_ExposesMetadataForEveryInstalledDefinition()
    {
        var registry = GameRegistry.Create([Fake("hidden-token-draft", 1), Fake("some-other-game", 1)]);

        registry.RegisteredDefinitions.Select(m => m.Slug).Should().BeEquivalentTo(["hidden-token-draft", "some-other-game"]);
    }
}
