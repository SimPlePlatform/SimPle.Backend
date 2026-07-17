namespace SimPle.Application.Realtime.Authorization;

/// <summary>
/// Allow/deny result for a realtime scope authorization check. Mirrors the Result/Result&lt;T&gt; Allow/Fail
/// static-factory convention already used across the Application layer (see SimPle.Shared.Common.Result), but is
/// intentionally its own type: authorization denials here always collapse to a single privacy-safe error code so
/// existence is never disclosed (docs/specs/module-07-realtime-presence-chat-spec.md, "privacy-safe: existence
/// never disclosed").
/// </summary>
public sealed class RealtimeScopeAuthorizationResult
{
    public bool IsAllowed { get; }
    public string? ErrorCode { get; }

    private RealtimeScopeAuthorizationResult(bool isAllowed, string? errorCode)
    {
        IsAllowed = isAllowed;
        ErrorCode = errorCode;
    }

    public static RealtimeScopeAuthorizationResult Allow() => new(true, null);

    public static RealtimeScopeAuthorizationResult Deny(string errorCode) => new(false, errorCode);
}
