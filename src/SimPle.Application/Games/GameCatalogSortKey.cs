using SimPle.Domain.Games;

namespace SimPle.Application.Games;

/// <summary>
/// Shared encode/decode contract for the four allow-listed catalog sorts' opaque cursor sort-key (paired with
/// <see cref="SimPle.Application.Common.Pagination.Cursor.EncodeCatalog"/> /
/// <see cref="SimPle.Application.Common.Pagination.Cursor.TryDecodeCatalog"/>). <see cref="GamesService"/>
/// encodes the last-emitted row's key into the outgoing cursor; <c>GameRepository</c> decodes an incoming
/// cursor's key back into the typed keyset predicate for whichever sort is active.
/// </summary>
public static class GameCatalogSortKey
{
    public const string Default = "default";
    public const string Name = "name";
    public const string Difficulty = "difficulty";
    public const string Duration = "duration";

    public static readonly IReadOnlyList<string> AllowedSorts = new[] { Default, Name, Difficulty, Duration };

    /// <summary>
    /// Stands in for a null <see cref="Game.FeaturedRank"/> so the default sort's key is a single, totally
    /// ordered, fixed-width string (Postgres ASC already sorts NULLS LAST, which this sentinel mirrors: it is
    /// larger than any real rank, so encoded null rows sort after every real rank when compared as text).
    /// </summary>
    public const int NoFeaturedRank = int.MaxValue;

    public static string Encode(string sort, Game game) => sort switch
    {
        Default => $"{(game.FeaturedRank ?? NoFeaturedRank):D10}{game.SortOrder:D10}",
        Name => game.Name.ToUpperInvariant(),
        Difficulty => $"{DifficultyRank(game.Difficulty):D2}",
        Duration => $"{game.EstimatedDurationMinMinutes:D10}",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown sort."),
    };

    /// <summary>Easy/Medium/Hard progression, independent of the column's string storage (see GameConfiguration).</summary>
    public static int DifficultyRank(GameDifficulty difficulty) => difficulty switch
    {
        GameDifficulty.Easy => 0,
        GameDifficulty.Medium => 1,
        GameDifficulty.Hard => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Unknown difficulty."),
    };

    public static bool TryDecodeDefault(string sortKey, out int featuredRank, out int sortOrder)
    {
        featuredRank = default;
        sortOrder = default;
        if (sortKey.Length != 20) return false;
        if (!int.TryParse(sortKey.AsSpan(0, 10), out featuredRank)) return false;
        if (!int.TryParse(sortKey.AsSpan(10, 10), out sortOrder)) return false;
        return true;
    }

    public static bool TryDecodeDifficulty(string sortKey, out int rank) =>
        int.TryParse(sortKey, out rank) && rank is >= 0 and <= 2;

    public static bool TryDecodeDuration(string sortKey, out int minutes) =>
        int.TryParse(sortKey, out minutes) && minutes >= 0;
}
