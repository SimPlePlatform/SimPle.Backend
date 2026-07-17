namespace SimPle.Application.Realtime.Authorization;

/// <summary>
/// Authorizes a user's action against a single realtime scope (a lobby, a match, ...). Every hub method that
/// touches a scope must call this — cached principal claims from handshake time are never trusted alone (see
/// "the load-bearing rule" in docs/specs/module-07-realtime-presence-chat-spec.md): membership, blocks, and
/// suspension can all change after a connection is already open, so every method rechecks current owner data.
/// </summary>
public interface IRealtimeScopeAuthorizer
{
    /// <summary>The scope kind this authorizer handles: "lobby" | "match" (see RealtimeEnvelope scope constants).</summary>
    string ScopeKind { get; }

    Task<RealtimeScopeAuthorizationResult> AuthorizeAsync(
        Guid actorUserId, Guid scopeId, RealtimeAction action, CancellationToken ct = default);
}
