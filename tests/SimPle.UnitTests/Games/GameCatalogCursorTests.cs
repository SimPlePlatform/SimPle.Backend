using FluentAssertions;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Games;
using SimPle.Domain.Games;

namespace SimPle.UnitTests.Games;

/// <summary>
/// Cursor.EncodeCatalog/TryDecodeCatalog round-trip and failure modes, plus GameCatalogSortKey's per-sort
/// encode/decode stability. No repository/service involved — see GamesServiceTests for the service-level
/// cursor validation (shape-hash binding, sort-key decodability against the active sort).
/// </summary>
public sealed class GameCatalogCursorTests
{
    private static Game MakeGame(
        string slug = "chess-lite", int? featuredRank = 1, int sortOrder = 3,
        GameDifficulty difficulty = GameDifficulty.Hard, int durationMin = 5) => Game.Create(
        slug: slug, name: "Chess Lite", summary: "A streamlined chess experience.",
        rulesSummary: "Chess Lite wins by checkmate.", difficulty: difficulty,
        estimatedDurationMinMinutes: durationMin, estimatedDurationMaxMinutes: durationMin + 20,
        minPlayers: 2, maxPlayers: 2, initialLifecycle: GameLifecycle.ComingSoon,
        featuredRank: featuredRank, sortOrder: sortOrder, artToken: "chess-lite",
        artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
        manifestVersion: "2026.1", category: "strategy", tags: Array.Empty<string>(),
        modes: Array.Empty<string>());

    // ── Cursor round-trip ────────────────────────────────────────────────────

    [Fact]
    public void EncodeCatalog_TryDecodeCatalog_RoundTrips()
    {
        var cursor = Cursor.EncodeCatalog("0000000001", "chess-lite", "abc123hash");

        var ok = Cursor.TryDecodeCatalog(cursor, out var sortKey, out var slug, out var shapeHash);

        ok.Should().BeTrue();
        sortKey.Should().Be("0000000001");
        slug.Should().Be("chess-lite");
        shapeHash.Should().Be("abc123hash");
    }

    [Fact]
    public void TryDecodeCatalog_Null_ReturnsFalse()
    {
        Cursor.TryDecodeCatalog(null, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecodeCatalog_MalformedBase64_ReturnsFalse()
    {
        Cursor.TryDecodeCatalog("not-valid-base64!!!", out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecodeCatalog_ForgedToken_FromADifferentCursorShape_ReturnsFalse()
    {
        // A cursor built by a different Encode* helper has the wrong segment count and must not decode.
        var foreignCursor = Cursor.EncodeStringId("someKey", Guid.NewGuid());

        Cursor.TryDecodeCatalog(foreignCursor, out _, out _, out _).Should().BeFalse();
    }

    // ── GameCatalogSortKey: encode/decode stability per sort ────────────────

    [Fact]
    public void Encode_Default_IsFixedWidthAndOrdersRankThenSortOrder()
    {
        var ranked = MakeGame(featuredRank: 1, sortOrder: 3);
        var unranked = MakeGame(featuredRank: null, sortOrder: 0);

        var rankedKey = GameCatalogSortKey.Encode(GameCatalogSortKey.Default, ranked);
        var unrankedKey = GameCatalogSortKey.Encode(GameCatalogSortKey.Default, unranked);

        rankedKey.Length.Should().Be(20);
        unrankedKey.Length.Should().Be(20);
        // Text ordering must mirror Postgres NULLS LAST: any real rank sorts before the null-tail sentinel.
        string.CompareOrdinal(rankedKey, unrankedKey).Should().BeLessThan(0);
    }

    [Fact]
    public void Encode_Default_RoundTripsThroughTryDecodeDefault()
    {
        var game = MakeGame(featuredRank: 7, sortOrder: 42);

        var key = GameCatalogSortKey.Encode(GameCatalogSortKey.Default, game);
        var ok = GameCatalogSortKey.TryDecodeDefault(key, out var rank, out var order);

        ok.Should().BeTrue();
        rank.Should().Be(7);
        order.Should().Be(42);
    }

    [Fact]
    public void Encode_Default_NullFeaturedRank_DecodesToSentinel()
    {
        var game = MakeGame(featuredRank: null, sortOrder: 5);

        var key = GameCatalogSortKey.Encode(GameCatalogSortKey.Default, game);
        GameCatalogSortKey.TryDecodeDefault(key, out var rank, out _).Should().BeTrue();

        rank.Should().Be(GameCatalogSortKey.NoFeaturedRank);
    }

    [Fact]
    public void Encode_Name_UppercasesTheName()
    {
        var key = GameCatalogSortKey.Encode(GameCatalogSortKey.Name, MakeGame());
        key.Should().Be("CHESS LITE");
    }

    [Theory]
    [InlineData(GameDifficulty.Easy, 0)]
    [InlineData(GameDifficulty.Medium, 1)]
    [InlineData(GameDifficulty.Hard, 2)]
    public void DifficultyRank_OrdersEasyMediumHard_RegardlessOfStringStorage(GameDifficulty difficulty, int expectedRank)
    {
        GameCatalogSortKey.DifficultyRank(difficulty).Should().Be(expectedRank);
    }

    [Fact]
    public void Encode_Difficulty_RoundTripsThroughTryDecodeDifficulty()
    {
        var game = MakeGame(difficulty: GameDifficulty.Medium);

        var key = GameCatalogSortKey.Encode(GameCatalogSortKey.Difficulty, game);
        GameCatalogSortKey.TryDecodeDifficulty(key, out var rank).Should().BeTrue();

        rank.Should().Be(1);
    }

    [Fact]
    public void TryDecodeDifficulty_OutOfRange_ReturnsFalse()
    {
        GameCatalogSortKey.TryDecodeDifficulty("99", out _).Should().BeFalse();
        GameCatalogSortKey.TryDecodeDifficulty("not-a-number", out _).Should().BeFalse();
    }

    [Fact]
    public void Encode_Duration_RoundTripsThroughTryDecodeDuration()
    {
        var game = MakeGame(durationMin: 15);

        var key = GameCatalogSortKey.Encode(GameCatalogSortKey.Duration, game);
        GameCatalogSortKey.TryDecodeDuration(key, out var minutes).Should().BeTrue();

        minutes.Should().Be(15);
    }

    [Fact]
    public void TryDecodeDuration_Negative_ReturnsFalse()
    {
        GameCatalogSortKey.TryDecodeDuration("-5", out _).Should().BeFalse();
    }
}
