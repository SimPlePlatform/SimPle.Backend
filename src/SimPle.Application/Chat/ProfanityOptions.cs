namespace SimPle.Application.Chat;

/// <summary>Backs <see cref="IChatProfanityFilter"/>. The deny list is a versioned server-side configuration
/// value (docs/module-requirements/module-07-realtime-presence-chat.md, "CUSTOM" security requirement) —
/// never a client-editable input.</summary>
public sealed class ProfanityOptions
{
    public const string SectionName = "Profanity";

    /// <summary>Bumped whenever <see cref="Terms"/> changes. Not surfaced to clients today; exists so this stays
    /// a versioned configuration value rather than an untracked ad hoc list.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Case-insensitive whole-word deny-list terms. Environment/appsettings-supplied only.</summary>
    public IReadOnlyList<string> Terms { get; set; } = Array.Empty<string>();
}
