namespace SimPle.Application.Games.DTOs;

/// <summary>
/// Private favorites-list / favorite-mutation DTO. Never shared-cached (<c>private, no-store</c>).
/// </summary>
public sealed record GameFavoriteDto(
    string Slug,
    string Name,
    string Lifecycle,
    string ArtToken,
    string ArtColorA,
    string ArtColorB,
    string ArtAltText,
    DateTime FavoritedAt);
