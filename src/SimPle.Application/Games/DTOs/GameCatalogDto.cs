namespace SimPle.Application.Games.DTOs;

/// <summary>
/// Public catalog/detail/featured DTO. Auth-independent — byte-identical for anonymous and authenticated
/// callers, which is what makes <c>Cache-Control: public, max-age=60</c> safe (see spec D1). Deliberately
/// excludes <c>isFavorited</c>, online/presence counts, <c>lastPlayed</c>, stats, and ELO — favorite state
/// comes only from the private favorites endpoint and is merged client-side.
/// </summary>
public sealed record GameCatalogDto(
    string Slug,
    string Name,
    string Summary,
    string RulesSummary,
    string Category,
    IReadOnlyList<string> Tags,
    string Difficulty,
    int EstimatedDurationMinMinutes,
    int EstimatedDurationMaxMinutes,
    int MinPlayers,
    int MaxPlayers,
    string Lifecycle,
    IReadOnlyList<string> Capabilities,
    int? FeaturedRank,
    string ArtToken,
    string ArtColorA,
    string ArtColorB,
    string ArtAltText,
    IReadOnlyList<GameEntryActionDto> EntryActions);
