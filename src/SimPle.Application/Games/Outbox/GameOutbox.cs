using System.Text.Json;
using SimPle.Domain.Games;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Games.Outbox;

/// <summary>
/// Builds the three Module 4 integration events as immutable <see cref="OutboxMessage"/> rows, mirroring
/// <see cref="SimPle.Application.Friends.Outbox.FriendOutbox"/>. Every payload carries minimum ids only — no
/// search text, no favorite contents. Each message is staged inside the same transaction as the aggregate
/// mutation; the unique (AggregateId, EventType, AggregateDomainVersion) index makes a retried transition
/// idempotent. No consumer exists yet (arrives with M7/M10/M11) — that is expected, not a gap.
/// </summary>
public static class GameOutbox
{
    public const int EventVersion = 1;
    private const string FavoriteAggregate = "UserFavoriteGame";
    private const string GameAggregate = "Game";

    public const string GameFavorited = "GameFavoritedV1";
    public const string GameUnfavorited = "GameUnfavoritedV1";
    public const string GameLifecycleChanged = "GameLifecycleChangedV1";

    public static OutboxMessage GameFavoritedEvent(UserFavoriteGame favorite) =>
        Favorite(favorite, GameFavorited);

    public static OutboxMessage GameUnfavoritedEvent(UserFavoriteGame favorite) =>
        Favorite(favorite, GameUnfavorited);

    public static OutboxMessage GameLifecycleChangedEvent(Game game) =>
        OutboxMessage.Create(
            GameAggregate, game.Id, GameLifecycleChanged, EventVersion,
            aggregateDomainVersion: game.LifecycleVersion, requestCycleId: game.LifecycleVersion,
            Serialize(new
            {
                gameId = game.Id,
                lifecycle = game.Lifecycle.ToString(),
            }));

    private static OutboxMessage Favorite(UserFavoriteGame favorite, string eventType) =>
        OutboxMessage.Create(
            FavoriteAggregate, favorite.Id, eventType, EventVersion,
            aggregateDomainVersion: favorite.CycleId, requestCycleId: favorite.CycleId,
            Serialize(new
            {
                favoriteId = favorite.Id,
                userId = favorite.UserId,
                gameId = favorite.GameId,
            }));

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload);
}
