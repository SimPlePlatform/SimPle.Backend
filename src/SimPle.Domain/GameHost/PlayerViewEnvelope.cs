namespace SimPle.Domain.GameHost;

/// <summary>
/// The only shape of game state a client is ever allowed to see. There is no default full-state serialization
/// to fall back on: a definition builds each viewer's projection explicitly, so forgetting to redact is a
/// compile-time absence of data rather than a runtime leak.
/// <para>
/// A spectator gets <see cref="PublicView"/> and nothing else — <see cref="PrivateView"/> is
/// <see langword="null"/> for them by construction, enforced in <see cref="Create"/>.
/// </para>
/// </summary>
public sealed class PlayerViewEnvelope
{
    public int Revision { get; }
    public ViewerRole ViewerRole { get; }

    /// <summary>The seat this view belongs to, or <see langword="null"/> for a spectator.</summary>
    public int? ViewerSeat { get; }

    /// <summary>Data every viewer may see.</summary>
    public ReadOnlyMemory<byte> PublicView { get; }

    /// <summary>Data only this seat may see (its hand, its hidden objective). Always null for a spectator.</summary>
    public ReadOnlyMemory<byte>? PrivateView { get; }

    public int ViewSchemaVersion { get; }

    /// <summary>Whether the engine considers the match finished, so a client can stop expecting commands.</summary>
    public EngineState EngineState { get; }

    private PlayerViewEnvelope(
        int revision,
        ViewerRole viewerRole,
        int? viewerSeat,
        ReadOnlyMemory<byte> publicView,
        ReadOnlyMemory<byte>? privateView,
        int viewSchemaVersion,
        EngineState engineState)
    {
        Revision = revision;
        ViewerRole = viewerRole;
        ViewerSeat = viewerSeat;
        PublicView = publicView;
        PrivateView = privateView;
        ViewSchemaVersion = viewSchemaVersion;
        EngineState = engineState;
    }

    public static PlayerViewEnvelope Create(
        int revision,
        ViewerContext viewer,
        ReadOnlySpan<byte> publicView,
        int viewSchemaVersion,
        EngineState engineState,
        ReadOnlySpan<byte> privateView = default,
        bool hasPrivateView = false)
    {
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");
        if (viewSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewSchemaVersion), viewSchemaVersion, "ViewSchemaVersion must be positive.");

        if (viewer.IsSpectator)
        {
            if (hasPrivateView)
                throw new ArgumentException("A spectator projection must not carry private data.", nameof(hasPrivateView));
            if (viewer.Seat is not null)
                throw new ArgumentException("A spectator has no seat.", nameof(viewer));
        }
        else if (viewer.Seat is null)
        {
            throw new ArgumentException("A player projection requires a seat.", nameof(viewer));
        }

        // Deliberately not a ternary. ReadOnlyMemory<byte> has an implicit conversion from byte[], and the null
        // literal converts to byte[], so `cond ? memory : null` takes ReadOnlyMemory<byte> as its natural type
        // and the null branch silently becomes an *empty* memory that then lifts to HasValue = true. A spectator
        // would report PrivateView as present-but-empty, and every `is not null` check downstream would read that
        // as "this viewer has private data". Absent must mean absent.
        ReadOnlyMemory<byte>? carriedPrivateView = null;
        if (hasPrivateView)
            carriedPrivateView = new ReadOnlyMemory<byte>(privateView.ToArray());

        return new PlayerViewEnvelope(
            revision,
            viewer.Role,
            viewer.Seat,
            publicView.ToArray(),
            carriedPrivateView,
            viewSchemaVersion,
            engineState);
    }
}
