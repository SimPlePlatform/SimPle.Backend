using System.Linq;
using FluentAssertions;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Games;
using SimPle.Application.Games.Services;
using SimPle.Domain.Games;

namespace SimPle.UnitTests.Games;

/// <summary>
/// Service-layer tests for <see cref="GamesService"/>: validation happens before any repository call, cursor
/// shape-binding rejects forged/malformed/mismatched tokens (including a cursor whose sort key cannot decode
/// under the currently-requested sort — the repository would otherwise silently ignore it and restart at page
/// 1), the ETag hash is stable/changes with row data, entryActions is the fixed 5-action projection on every
/// DTO regardless of lifecycle, and favorites are idempotent per the spec's PUT/DELETE contract.
/// </summary>
public sealed class GamesServiceTests
{
    private readonly IGameRepository _games = Substitute.For<IGameRepository>();
    private readonly GamesService _service;

    private static readonly IReadOnlyList<(Guid, string, string)> NoExtras = Array.Empty<(Guid, string, string)>();

    public GamesServiceTests()
    {
        _service = new GamesService(_games);
        _games.GetTagsAndCapabilitiesAsync(Arg.Any<IReadOnlyList<Guid>>()).Returns(NoExtras);
    }

    private static Game MakeGame(
        string slug = "chess-lite", GameLifecycle lifecycle = GameLifecycle.ComingSoon,
        int? featuredRank = null) => Game.Create(
        slug: slug, name: "Chess Lite", summary: "A streamlined chess experience.",
        rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
        estimatedDurationMinMinutes: 5, estimatedDurationMaxMinutes: 25,
        minPlayers: 2, maxPlayers: 2, initialLifecycle: lifecycle,
        featuredRank: featuredRank, sortOrder: 1, artToken: "chess-lite",
        artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
        manifestVersion: "2026.1", category: "strategy", tags: Array.Empty<string>(),
        modes: Array.Empty<string>());

    // ── List: validation rejected before touching the database ─────────────

    [Fact]
    public async Task ListAsync_SearchTooShort_ValidationFailed_NeverQueriesRepository()
    {
        var result = await _service.ListAsync("a", null, null, null, null, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
        await _games.DidNotReceive().GetCatalogPageAsync(Arg.Any<GameCatalogFilter>());
    }

    [Fact]
    public async Task ListAsync_EmptyOrWhitespaceQuery_IsTreatedAsNoSearchFilter_NotAValidationError()
    {
        _games.GetCatalogPageAsync(Arg.Any<GameCatalogFilter>()).Returns(new List<Game>());

        var result = await _service.ListAsync("   ", null, null, null, null, null, 24, null);

        result.IsSuccess.Should().BeTrue();
        await _games.Received(1).GetCatalogPageAsync(Arg.Is<GameCatalogFilter>(f => f.NormalizedSearch == null));
    }

    [Fact]
    public async Task ListAsync_SearchTooLong_ValidationFailed()
    {
        var result = await _service.ListAsync(new string('a', 101), null, null, null, null, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task ListAsync_TooManyCategoryValues_ValidationFailed()
    {
        var categories = Enumerable.Range(0, 6).Select(_ => "puzzle").ToArray();

        var result = await _service.ListAsync(null, categories, null, null, null, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
        await _games.DidNotReceive().GetCatalogPageAsync(Arg.Any<GameCatalogFilter>());
    }

    [Fact]
    public async Task ListAsync_UnknownCategory_ValidationFailed()
    {
        var result = await _service.ListAsync(null, new[] { "not-a-real-category" }, null, null, null, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task ListAsync_UnknownMode_ValidationFailed()
    {
        var result = await _service.ListAsync(null, null, null, new[] { "not-a-real-mode" }, null, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Retired")]
    [InlineData("NotARealLifecycle")]
    public async Task ListAsync_NonPublicLifecycleFilter_ValidationFailed(string lifecycle)
    {
        var result = await _service.ListAsync(null, null, null, null, new[] { lifecycle }, null, 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task ListAsync_UnknownSort_ValidationFailed()
    {
        var result = await _service.ListAsync(null, null, null, null, null, "not-a-sort", 24, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task ListAsync_PageSizeOutOfBounds_ValidationFailed(int limit)
    {
        var result = await _service.ListAsync(null, null, null, null, null, null, limit, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
        await _games.DidNotReceive().GetCatalogPageAsync(Arg.Any<GameCatalogFilter>());
    }

    // ── List: cursor shape binding ───────────────────────────────────────────

    [Fact]
    public async Task ListAsync_MalformedCursor_InvalidCursor()
    {
        var result = await _service.ListAsync(null, null, null, null, null, null, 24, "not-a-valid-cursor!!!");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task ListAsync_CursorFromADifferentFilterShape_InvalidCursor()
    {
        _games.GetCatalogPageAsync(Arg.Any<GameCatalogFilter>())
            .Returns(new List<Game> { MakeGame() });

        var firstPage = await _service.ListAsync("chess", null, null, null, null, null, 1, null);
        var cursorForChessSearch = firstPage.Value!.Page.NextCursor;

        // Re-issue the exact same cursor but drop the search term — a different normalized query shape.
        var result = await _service.ListAsync(null, null, null, null, null, null, 1, cursorForChessSearch);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task ListAsync_CursorSortKeyInvalidForActiveSort_InvalidCursor_DoesNotSilentlyRestart()
    {
        _games.GetCatalogPageAsync(Arg.Any<GameCatalogFilter>())
            .Returns(new List<Game> { MakeGame() });

        // Build a cursor for the default sort, then replay it against sort=name — the repository's name-sort
        // branch would happily treat a non-empty sortKey as valid, but the *value* was minted under a
        // different sort's key format, so the service must still recompute the shape hash and reject it.
        var defaultPage = await _service.ListAsync(null, null, null, null, null, GameCatalogSortKey.Default, 1, null);
        var cursorFromDefaultSort = defaultPage.Value!.Page.NextCursor;

        var result = await _service.ListAsync(null, null, null, null, null, GameCatalogSortKey.Name, 1, cursorFromDefaultSort);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task ListAsync_NoLifecycleFilter_DefaultsToThePublicThreeLifecycles()
    {
        _games.GetCatalogPageAsync(Arg.Any<GameCatalogFilter>()).Returns(new List<Game>());

        await _service.ListAsync(null, null, null, null, null, null, 24, null);

        await _games.Received(1).GetCatalogPageAsync(Arg.Is<GameCatalogFilter>(f =>
            f.Lifecycles.Count == 3 &&
            f.Lifecycles.Contains(GameLifecycle.ComingSoon) &&
            f.Lifecycles.Contains(GameLifecycle.Available) &&
            f.Lifecycles.Contains(GameLifecycle.Maintenance)));
    }

    // ── entryActions: fixed projection regardless of lifecycle ──────────────

    [Theory]
    [InlineData(GameLifecycle.ComingSoon)]
    [InlineData(GameLifecycle.Available)]
    [InlineData(GameLifecycle.Maintenance)]
    public async Task GetDetailAsync_EntryActions_IsTheFixedFiveActionProjection(GameLifecycle lifecycle)
    {
        var game = MakeGame(lifecycle: lifecycle);
        _games.GetBySlugAsync(game.Slug).Returns(game);

        var result = await _service.GetDetailAsync(game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Game!.EntryActions.Should().BeEquivalentTo(GameEntryActions.All);
        // M9/M8 haven't shipped yet; M6's 3 owned actions flipped to enabled once its own gate passed.
        result.Value.Game.EntryActions.Where(a => a.OwnerModule != 6).Should().OnlyContain(a => a.Status == "deferred");
        result.Value.Game.EntryActions.Where(a => a.OwnerModule == 6).Should().OnlyContain(a => a.Status == "enabled");
    }

    // ── Detail: 404 / 410 / 200 per lifecycle ────────────────────────────────

    [Fact]
    public async Task GetDetailAsync_UnknownSlug_NotFound()
    {
        _games.GetBySlugAsync("unknown-slug").Returns((Game?)null);

        var result = await _service.GetDetailAsync("unknown-slug");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Games.NotFound");
    }

    [Fact]
    public async Task GetDetailAsync_Draft_NotFound_IndistinguishableFromUnknown()
    {
        var game = MakeGame(lifecycle: GameLifecycle.Draft);
        _games.GetBySlugAsync(game.Slug).Returns(game);

        var result = await _service.GetDetailAsync(game.Slug);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Games.NotFound");
    }

    [Fact]
    public async Task GetDetailAsync_Retired_ReturnsMinimalTombstone_NoETag()
    {
        var game = MakeGame(lifecycle: GameLifecycle.ComingSoon);
        game.MakeAvailable();
        game.Retire();
        _games.GetBySlugAsync(game.Slug).Returns(game);

        var result = await _service.GetDetailAsync(game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Game.Should().BeNull();
        result.Value.ETag.Should().BeNull();
        result.Value.Tombstone.Should().NotBeNull();
        result.Value.Tombstone!.Slug.Should().Be(game.Slug);
        result.Value.Tombstone.ReasonCode.Should().Be("Games.Retired");
    }

    // ── ETag: stable for the same data, changes when it changes ─────────────

    [Fact]
    public async Task GetDetailAsync_SameGameData_ProducesTheSameETagAcrossCalls()
    {
        var game = MakeGame();
        _games.GetBySlugAsync(game.Slug).Returns(game);

        var first = await _service.GetDetailAsync(game.Slug);
        var second = await _service.GetDetailAsync(game.Slug);

        first.Value!.ETag.Should().Be(second.Value!.ETag);
    }

    [Fact]
    public async Task GetDetailAsync_AfterLifecycleTransition_ETagChanges()
    {
        var game = MakeGame();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        var before = await _service.GetDetailAsync(game.Slug);

        game.MakeAvailable(); // bumps LifecycleVersion + UpdatedAt

        var after = await _service.GetDetailAsync(game.Slug);
        after.Value!.ETag.Should().NotBe(before.Value!.ETag);
    }

    // ── Featured ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFeaturedAsync_NoneFeatured_ReturnsNullGameAndNullETag()
    {
        _games.GetFeaturedAsync().Returns((Game?)null);

        var result = await _service.GetFeaturedAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Game.Should().BeNull();
        result.Value.ETag.Should().BeNull();
    }

    [Fact]
    public async Task GetFeaturedAsync_Featured_ReturnsDtoAndETag()
    {
        var game = MakeGame(featuredRank: 1);
        _games.GetFeaturedAsync().Returns(game);

        var result = await _service.GetFeaturedAsync();

        result.Value!.Game.Should().NotBeNull();
        result.Value.ETag.Should().NotBeNullOrEmpty();
    }

    // ── Favorites: PUT ────────────────────────────────────────────────────────

    [Fact]
    public async Task PutFavoriteAsync_UnknownSlug_NotFound()
    {
        _games.GetBySlugAsync("unknown-slug").Returns((Game?)null);

        var result = await _service.PutFavoriteAsync(Guid.NewGuid(), "unknown-slug");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Games.NotFound");
    }

    [Fact]
    public async Task PutFavoriteAsync_NewFavorite_AddsAndReturnsDto()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns((UserFavoriteGame?)null);
        _games.AddFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>())
            .Returns(AddFavoriteOutcome.Added);

        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Slug.Should().Be(game.Slug);
        await _games.Received(1).AddFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    [Fact]
    public async Task PutFavoriteAsync_AlreadyActiveFavorite_IsIdempotent_NoRepositoryWrite()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var existing = UserFavoriteGame.Favorite(userId, game.Id);
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns(existing);

        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Slug.Should().Be(game.Slug);
        await _games.DidNotReceive().AddFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
        await _games.DidNotReceive().UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    [Fact]
    public async Task PutFavoriteAsync_InactiveFavorite_Refavorites()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var existing = UserFavoriteGame.Favorite(userId, game.Id);
        existing.Unfavorite();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns(existing);
        _games.UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>())
            .Returns(UpdateFavoriteOutcome.Updated);

        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        existing.IsActive.Should().BeTrue();
        await _games.Received(1).UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    [Fact]
    public async Task PutFavoriteAsync_NewFavoriteOfRetiredGame_Conflict()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        game.MakeAvailable();
        game.Retire();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns((UserFavoriteGame?)null);

        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Games.Retired");
    }

    [Fact]
    public async Task PutFavoriteAsync_ExistingOwnerStillSeesTombstone_EvenAfterGameRetires()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var existing = UserFavoriteGame.Favorite(userId, game.Id);
        game.MakeAvailable();
        game.Retire();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns(existing);

        // An existing active favorite on a now-retired game is still idempotently reported back (retained
        // per spec: "Retired game retains an existing owner's favorite row").
        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Lifecycle.Should().Be("Retired");
    }

    [Fact]
    public async Task PutFavoriteAsync_AddConflict_ReReadsAndConverges()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var winningRow = UserFavoriteGame.Favorite(userId, game.Id);
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns((UserFavoriteGame?)null, winningRow);
        _games.AddFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>())
            .Returns(AddFavoriteOutcome.Conflict);

        var result = await _service.PutFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Slug.Should().Be(game.Slug);
    }

    // ── Favorites: DELETE ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFavoriteAsync_UnknownSlug_NotFound()
    {
        _games.GetBySlugAsync("unknown-slug").Returns((Game?)null);

        var result = await _service.DeleteFavoriteAsync(Guid.NewGuid(), "unknown-slug");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Games.NotFound");
    }

    [Fact]
    public async Task DeleteFavoriteAsync_NoExistingRow_IsIdempotentSuccess_NoWrite()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns((UserFavoriteGame?)null);

        var result = await _service.DeleteFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        await _games.DidNotReceive().UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    [Fact]
    public async Task DeleteFavoriteAsync_AlreadyInactive_IsIdempotentSuccess_NoWrite()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var existing = UserFavoriteGame.Favorite(userId, game.Id);
        existing.Unfavorite();
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns(existing);

        var result = await _service.DeleteFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        await _games.DidNotReceive().UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    [Fact]
    public async Task DeleteFavoriteAsync_ActiveFavorite_UnfavoritesAndWrites()
    {
        var userId = Guid.NewGuid();
        var game = MakeGame();
        var existing = UserFavoriteGame.Favorite(userId, game.Id);
        _games.GetBySlugAsync(game.Slug).Returns(game);
        _games.GetFavoriteAsync(userId, game.Id).Returns(existing);
        _games.UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>())
            .Returns(UpdateFavoriteOutcome.Updated);

        var result = await _service.DeleteFavoriteAsync(userId, game.Slug);

        result.IsSuccess.Should().BeTrue();
        existing.IsActive.Should().BeFalse();
        await _games.Received(1).UpdateFavoriteAsync(Arg.Any<UserFavoriteGame>(), Arg.Any<SimPle.Domain.Outbox.OutboxMessage>());
    }

    // ── Favorites list: validation ────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task GetFavoritesAsync_PageSizeOutOfBounds_ValidationFailed(int limit)
    {
        var result = await _service.GetFavoritesAsync(Guid.NewGuid(), limit, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task GetFavoritesAsync_MalformedCursor_InvalidCursor()
    {
        var result = await _service.GetFavoritesAsync(Guid.NewGuid(), 24, "not-a-valid-cursor!!!");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }
}
