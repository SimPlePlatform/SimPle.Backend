namespace SimPle.Application.Chat;

/// <summary>docs/specs/module-07-realtime-presence-chat-spec.md, API contract: "direction: before (scrollback,
/// default) | after (reconnect repair)."</summary>
public enum ChatHistoryDirection
{
    /// <summary>Scrollback. No cursor => the most recent page. With a cursor => the page immediately older than
    /// it. Default.</summary>
    Before,

    /// <summary>Reconnect repair: the page immediately newer than the cursor.</summary>
    After,
}
