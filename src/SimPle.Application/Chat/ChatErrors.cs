namespace SimPle.Application.Chat;

/// <summary>Error catalogue for Module 7 chat (docs/specs/module-07-realtime-presence-chat-spec.md, "Error
/// catalogue"). PascalCase dot-namespaced codes, matching every other module's per-module error class.</summary>
public static class ChatErrors
{
    /// <summary>Message absent or not visible to the viewer. Privacy-safe: existence is never disclosed, so an
    /// unauthorized viewer and a genuinely-missing message collapse to the same code.</summary>
    public const string NotFound = "Chat.NotFound";

    /// <summary>The one legitimate distinguishing code in this module: not the author on delete.</summary>
    public const string Forbidden = "Chat.Forbidden";

    /// <summary>Failed normalization/length/control-character rules.</summary>
    public const string InvalidBody = "Chat.InvalidBody";

    /// <summary>Deny-list match. A normal validation error, not a security event — message is not stored, sender
    /// is told why.</summary>
    public const string ProfanityRejected = "Chat.ProfanityRejected";

    /// <summary>Hold requested (M12) on a message that already aged out of retention.</summary>
    public const string MessageExpired = "Chat.MessageExpired";

    /// <summary>Shared literal with every other module's own rate-limit constant — same string, independently
    /// declared, matching this codebase's per-module-duplicate-constant convention.</summary>
    public const string RateLimitExceeded = "RateLimit.Exceeded";

    /// <summary>Shared cross-module literal (matches <c>LobbyErrors.ValidationFailed</c>).</summary>
    public const string ValidationFailed = "Validation.Failed";

    /// <summary>Shared cross-module literal (matches <c>LobbyErrors.InvalidCursor</c>).</summary>
    public const string InvalidCursor = "Pagination.InvalidCursor";
}
