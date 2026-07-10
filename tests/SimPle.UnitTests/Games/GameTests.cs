using FluentAssertions;
using SimPle.Domain.Games;

namespace SimPle.UnitTests.Games;

/// <summary>
/// Pure in-memory domain tests for <see cref="Game"/>: lifecycle state machine edges, the Draft/Retired
/// cannot-be-featured guard, and the Create/ApplyManifestUpdate validation invariants. No database involved —
/// see tests/SimPle.IntegrationTests/Games for the real-Postgres CHECK/index verification.
/// </summary>
public sealed class GameTests
{
    private static Game ValidComingSoonGame(string slug = "chess-lite") => Game.Create(
        slug: slug,
        name: "Chess Lite",
        summary: "A streamlined chess experience.",
        rulesSummary: "Chess Lite wins by checkmate or a future configured clock.",
        difficulty: GameDifficulty.Hard,
        estimatedDurationMinMinutes: 5,
        estimatedDurationMaxMinutes: 25,
        minPlayers: 2,
        maxPlayers: 2,
        initialLifecycle: GameLifecycle.ComingSoon,
        featuredRank: 1,
        sortOrder: 3,
        artToken: "chess-lite",
        artColorA: "#9B51E0",
        artColorB: "#2D9CDB",
        artAltText: "Chess Lite abstract game artwork",
        manifestVersion: "2026.1",
        category: "strategy",
        tags: Array.Empty<string>(),
        modes: new[] { "ai", "multiplayer", "ranked" });

    // ── Lifecycle: legal edges ──────────────────────────────────────────────

    [Fact]
    public void Publish_FromDraft_TransitionsToComingSoon()
    {
        var game = Game.Create(
            "draft-game", "Draft Game", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 1, 2, GameLifecycle.Draft, null, 0,
            "draft-game", "#111111", "#222222", "Draft Game abstract game artwork", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "solo" });

        game.Publish();

        game.Lifecycle.Should().Be(GameLifecycle.ComingSoon);
    }

    [Fact]
    public void MakeAvailable_FromComingSoon_TransitionsToAvailable()
    {
        var game = ValidComingSoonGame();

        game.MakeAvailable();

        game.Lifecycle.Should().Be(GameLifecycle.Available);
    }

    [Fact]
    public void EnterMaintenance_FromAvailable_TransitionsToMaintenance()
    {
        var game = ValidComingSoonGame();
        game.MakeAvailable();

        game.EnterMaintenance();

        game.Lifecycle.Should().Be(GameLifecycle.Maintenance);
    }

    [Fact]
    public void MakeAvailable_FromMaintenance_TransitionsToAvailable()
    {
        var game = ValidComingSoonGame();
        game.MakeAvailable();
        game.EnterMaintenance();

        game.MakeAvailable();

        game.Lifecycle.Should().Be(GameLifecycle.Available);
    }

    [Theory]
    [InlineData(GameLifecycle.Draft)]
    [InlineData(GameLifecycle.ComingSoon)]
    [InlineData(GameLifecycle.Available)]
    [InlineData(GameLifecycle.Maintenance)]
    public void Retire_FromAnyNonRetiredState_TransitionsToRetired(GameLifecycle from)
    {
        var game = GameAt(from);

        game.Retire();

        game.Lifecycle.Should().Be(GameLifecycle.Retired);
    }

    // ── Lifecycle: illegal edges throw ──────────────────────────────────────

    [Fact]
    public void MakeAvailable_FromDraft_Throws()
    {
        var game = GameAt(GameLifecycle.Draft);
        var act = () => game.MakeAvailable();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnterMaintenance_FromDraft_Throws()
    {
        var game = GameAt(GameLifecycle.Draft);
        var act = () => game.EnterMaintenance();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnterMaintenance_FromComingSoon_Throws()
    {
        var game = GameAt(GameLifecycle.ComingSoon);
        var act = () => game.EnterMaintenance();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Publish_FromComingSoon_Throws()
    {
        var game = GameAt(GameLifecycle.ComingSoon);
        var act = () => game.Publish();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Publish_FromAvailable_Throws()
    {
        var game = GameAt(GameLifecycle.Available);
        var act = () => game.Publish();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnterMaintenance_FromMaintenance_Throws()
    {
        var game = GameAt(GameLifecycle.Maintenance);
        var act = () => game.EnterMaintenance();
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(GameLifecycle.Draft)]
    [InlineData(GameLifecycle.ComingSoon)]
    [InlineData(GameLifecycle.Available)]
    [InlineData(GameLifecycle.Maintenance)]
    [InlineData(GameLifecycle.Retired)]
    public void Retired_IsTerminal_NothingTransitionsOut(GameLifecycle _)
    {
        var game = GameAt(GameLifecycle.Retired);

        game.Invoking(g => g.Publish()).Should().Throw<InvalidOperationException>();
        game.Invoking(g => g.MakeAvailable()).Should().Throw<InvalidOperationException>();
        game.Invoking(g => g.EnterMaintenance()).Should().Throw<InvalidOperationException>();
        game.Invoking(g => g.Retire()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Retire_ClearsFeaturedRank()
    {
        var game = ValidComingSoonGame(); // seeded with FeaturedRank = 1
        game.FeaturedRank.Should().Be(1);

        game.Retire();

        game.FeaturedRank.Should().BeNull();
    }

    [Fact]
    public void TransitionTo_DispatchesToTheCorrectNamedMethod()
    {
        var game = GameAt(GameLifecycle.Draft);

        game.TransitionTo(GameLifecycle.ComingSoon);
        game.Lifecycle.Should().Be(GameLifecycle.ComingSoon);

        game.TransitionTo(GameLifecycle.Available);
        game.Lifecycle.Should().Be(GameLifecycle.Available);

        game.TransitionTo(GameLifecycle.Retired);
        game.Lifecycle.Should().Be(GameLifecycle.Retired);
    }

    private static Game GameAt(GameLifecycle target)
    {
        var game = Game.Create(
            $"game-{target}".ToLowerInvariant(), "Game", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 1, 2, GameLifecycle.Draft, null, 0,
            "art-token", "#111111", "#222222", "Game abstract game artwork", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "solo" });

        switch (target)
        {
            case GameLifecycle.Draft:
                return game;
            case GameLifecycle.ComingSoon:
                game.Publish();
                return game;
            case GameLifecycle.Available:
                game.Publish();
                game.MakeAvailable();
                return game;
            case GameLifecycle.Maintenance:
                game.Publish();
                game.MakeAvailable();
                game.EnterMaintenance();
                return game;
            case GameLifecycle.Retired:
                game.Retire();
                return game;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }

    // ── Create / ApplyManifestUpdate validation ─────────────────────────────

    [Fact]
    public void Create_RejectsMinPlayersLessThanOne()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 0, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "solo" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsMinPlayersGreaterThanMaxPlayers()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 3, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "solo" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsDurationMinGreaterThanMax()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            10, 5, 1, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "solo" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsNonAllowListedTag()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 1, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "puzzle", new[] { "not-a-real-tag" }, new[] { "solo" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsNonAllowListedMode()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 1, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "puzzle", new[] { "logic" }, new[] { "not-a-real-mode" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsRankedWithoutMultiplayer()
    {
        var act = () => Game.Create(
            "slug", "Name", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, 2, 2, GameLifecycle.ComingSoon, null, 0,
            "art", "#111111", "#222222", "alt text", "2026.1",
            "strategy", new[] { "classic" }, new[] { "ranked" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_AcceptsValidFullGame()
    {
        var game = ValidComingSoonGame();

        game.Slug.Should().Be("chess-lite");
        game.Lifecycle.Should().Be(GameLifecycle.ComingSoon);
        game.FeaturedRank.Should().Be(1);
        game.Category.Should().Be("strategy");
        game.Tags.Should().BeEmpty();
        game.Capabilities.Select(c => c.Mode).Should().BeEquivalentTo("ai", "multiplayer", "ranked");
    }

    [Fact]
    public void ApplyManifestUpdate_RejectsMinPlayersLessThanOne()
    {
        var game = ValidComingSoonGame();
        var act = () => game.ApplyManifestUpdate(
            "Chess Lite", "Summary.", "Rules.", GameDifficulty.Hard, 5, 25, 0, 2, 1, 3,
            "chess-lite", "#9B51E0", "#2D9CDB", "alt text", "2026.2",
            "strategy", new[] { "classic" }, new[] { "ai", "multiplayer", "ranked" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplyManifestUpdate_RejectsNonAllowListedTag()
    {
        var game = ValidComingSoonGame();
        var act = () => game.ApplyManifestUpdate(
            "Chess Lite", "Summary.", "Rules.", GameDifficulty.Hard, 5, 25, 2, 2, 1, 3,
            "chess-lite", "#9B51E0", "#2D9CDB", "alt text", "2026.2",
            "strategy", new[] { "not-a-real-tag" }, new[] { "ai", "multiplayer", "ranked" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplyManifestUpdate_LeavesSlugAndLifecycleUnchanged_AndReplacesFields()
    {
        var game = ValidComingSoonGame();
        game.MakeAvailable();

        game.ApplyManifestUpdate(
            "Chess Lite Updated", "New summary.", "New rules.", GameDifficulty.Medium, 10, 20, 2, 2, null, 9,
            "chess-lite-v2", "#000000", "#FFFFFF", "new alt text", "2026.2",
            "strategy", new[] { "classic" }, new[] { "multiplayer" });

        game.Slug.Should().Be("chess-lite");
        game.Lifecycle.Should().Be(GameLifecycle.Available);
        game.Name.Should().Be("Chess Lite Updated");
        game.FeaturedRank.Should().BeNull();
        game.SortOrder.Should().Be(9);
        game.ManifestVersion.Should().Be("2026.2");
        game.Category.Should().Be("strategy");
        game.Tags.Select(t => t.Value).Should().BeEquivalentTo("classic");
        game.Capabilities.Select(c => c.Mode).Should().BeEquivalentTo("multiplayer");
    }
}
