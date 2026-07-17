using SimPle.Application.Realtime.Contracts;

namespace SimPle.Application.Realtime.Authorization;

/// <summary>
/// Match-scope realtime does not exist yet — there is no Module 8 (matches) to authorize against. This
/// authorizer always denies with <c>realtime.scope_not_available</c> so the hub can declare the "match" scope
/// kind in its routing without special-casing "scope kind doesn't exist" as a distinct code path.
/// </summary>
public sealed class NullMatchScopeAuthorizer : IRealtimeScopeAuthorizer
{
    public const string ScopeNotAvailableCode = "realtime.scope_not_available";

    public string ScopeKind => RealtimeEnvelope.MatchScope;

    public Task<RealtimeScopeAuthorizationResult> AuthorizeAsync(
        Guid actorUserId, Guid scopeId, RealtimeAction action, CancellationToken ct = default) =>
        Task.FromResult(RealtimeScopeAuthorizationResult.Deny(ScopeNotAvailableCode));
}
