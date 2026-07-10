namespace SimPle.Application.Common.Options;

public sealed class DismissedSuggestionCleanupOptions
{
    public const string SectionName = "DismissedSuggestionCleanup";

    // How often the cleanup job runs (dismissals suppress a suggestion for 30 days, so daily is ample).
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    // Rows deleted per DB round-trip; the job loops in bounded batches until a short page is returned.
    public int BatchSize { get; set; } = 500;
}
