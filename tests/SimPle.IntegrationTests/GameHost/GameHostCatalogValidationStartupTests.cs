using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.Games;
using SimPle.UnitTests.GameHost.Reference;

namespace SimPle.IntegrationTests.GameHost;

/// <summary>
/// Program.cs's D1 startup drift check (<see cref="ICatalogEngineCompatibilityValidator"/>) only runs its
/// database round trip when at least one engine is installed (see the "skipped entirely while zero engines are
/// installed" comment in Program.cs); <see cref="GameHostCompositionRootTests.DefaultCompositionRoot_WithZeroInstalledEngines_ResolvesAnEmptyRegistry"/>
/// already covers that zero-engine branch. These tests cover the two branches that only exist once an engine is
/// installed: a matching catalog row lets the host boot, and a drifted one fails the host closed at startup
/// rather than letting a broken engine/catalog pairing serve traffic. Catalog rows must be seeded through a raw
/// <see cref="Infrastructure.Persistence.AppDbContext"/> pointed at the same InMemory database name — not through
/// the factory's own DI container — because Program.cs's compatibility check runs during host construction,
/// before any test code gets a chance to seed via the usual post-boot DI-resolved context.
/// </summary>
public sealed class GameHostCatalogValidationStartupTests
{
    private static HostedGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView> ReferenceEngine() =>
        new(new HiddenTokenDraftDefinition());

    [Fact]
    public void CatalogRowCompatibleWithTheInstalledEngine_HostBootsSuccessfully()
    {
        using var factory = new GameHostTestWebApplicationFactory(ReferenceEngine());
        SeedGame(factory, HiddenTokenDraftDefinition.Slug, minPlayers: 2, maxPlayers: 4, "multiplayer");

        var act = () => factory.Services.GetRequiredService<IGameRegistry>();

        act.Should().NotThrow();
    }

    [Fact]
    public void CatalogRowAdvertisesAModeTheInstalledEngineDoesNotSupport_HostStartupFailsClosed()
    {
        using var factory = new GameHostTestWebApplicationFactory(ReferenceEngine());
        SeedGame(factory, HiddenTokenDraftDefinition.Slug, minPlayers: 2, maxPlayers: 4, "multiplayer", "cooperative");

        var act = () => factory.Services.GetRequiredService<IGameRegistry>();

        var thrown = act.Should().Throw<Exception>().Which;
        FullChainText(thrown).Should().Contain(
            "compatibility check failed", "an engine/catalog drift must fail the host closed at startup, not serve traffic silently");
    }

    [Fact]
    public void CatalogRowAdvertisesANarrowerPlayerRangeThanTheEngineRequires_HostStartupFailsClosed()
    {
        using var factory = new GameHostTestWebApplicationFactory(ReferenceEngine());
        // The engine requires 2-4 players; a catalog row promising a 5-player match is drift in the other
        // direction from the mode case above (an engine capability gap rather than a stricter engine minimum).
        SeedGame(factory, HiddenTokenDraftDefinition.Slug, minPlayers: 2, maxPlayers: 5, "multiplayer");

        var act = () => factory.Services.GetRequiredService<IGameRegistry>();

        var thrown = act.Should().Throw<Exception>().Which;
        FullChainText(thrown).Should().Contain("compatibility check failed");
    }

    private static void SeedGame(
        GameHostTestWebApplicationFactory factory, string slug, int minPlayers, int maxPlayers, params string[] modes)
    {
        using var db = factory.OpenDbContext();
        db.Games.Add(Game.Create(
            slug, "Test Game", "A test game summary.", "Test rules summary.", GameDifficulty.Medium,
            5, 25, minPlayers, maxPlayers, GameLifecycle.Available, null, 0,
            "art-token", "#111111", "#222222", "Test game abstract artwork", "2026.1",
            "strategy", Array.Empty<string>(), modes));
        db.SaveChanges();
    }

    private static string FullChainText(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.AppendLine(e.Message);
        return sb.ToString();
    }
}
