namespace SimPle.Application.Chat;

/// <summary>Backs <see cref="SimPle.Infrastructure.Chat.ChatRetentionSweeper"/> (docs/specs/
/// module-07-realtime-presence-chat-spec.md, "Ownership, retention, deletion" and Risk #5). Mirrors
/// <c>TokenCleanupOptions</c>'s shape: an interval plus a per-run bound, both environment/appsettings-supplied.
/// </summary>
public sealed class ChatRetentionOptions
{
    public const string SectionName = "ChatRetention";

    /// <summary>How often the sweep runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows deleted per sweep pass. Bounded on purpose — the sweep is one statement per batch
    /// (<c>LIMIT</c> + <c>FOR UPDATE OF m SKIP LOCKED</c>), so a large backlog is drained over several passes
    /// rather than one unbounded transaction.</summary>
    public int BatchSize { get; set; } = 500;
}
