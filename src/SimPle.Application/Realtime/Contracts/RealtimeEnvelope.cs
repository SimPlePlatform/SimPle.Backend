namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// Every server-&gt;client realtime event carries this envelope (docs/specs/module-07-realtime-presence-chat-spec.md,
/// API Contract). <see cref="Scope"/> is one of <see cref="LobbyScope"/>, <see cref="MatchScope"/>,
/// <see cref="UserScope"/> — never anything else.
/// </summary>
/// <param name="SchemaVersion">Forward-compatibility marker for the envelope/payload shape.</param>
/// <param name="EventId">Unique per emitted event; used for client-side dedupe.</param>
/// <param name="ServerUtc">The server's clock at emission time. Never used for security decisions.</param>
/// <param name="Scope">"lobby" | "match" | "user".</param>
/// <param name="ScopeId">The lobby id, match id, or user id the event concerns.</param>
public sealed record RealtimeEnvelope(int SchemaVersion, Guid EventId, DateTime ServerUtc, string Scope, Guid ScopeId)
{
    public const string LobbyScope = "lobby";
    public const string MatchScope = "match";
    public const string UserScope = "user";

    public const int CurrentSchemaVersion = 1;

    public static RealtimeEnvelope ForLobby(Guid lobbyId, DateTime nowUtc) =>
        new(CurrentSchemaVersion, Guid.NewGuid(), nowUtc, LobbyScope, lobbyId);

    public static RealtimeEnvelope ForUser(Guid userId, DateTime nowUtc) =>
        new(CurrentSchemaVersion, Guid.NewGuid(), nowUtc, UserScope, userId);
}
