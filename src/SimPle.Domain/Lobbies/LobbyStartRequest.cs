using SimPle.Domain.Common;

namespace SimPle.Domain.Lobbies;

/// <summary>
/// The durable record of one attempt to start a lobby. <c>Open -&gt; Succeeded|Failed</c>.
///
/// A committed start request means the <c>MatchRequestedV1</c> outbox row is durable — it does <em>not</em> mean a
/// match exists (Risk #6). <see cref="MatchRequestId"/> is the correlation M8 echoes back on
/// <c>MatchCreatedV1</c>/<c>MatchCreationFailedV1</c>.
///
/// A partial unique index on <c>(LobbyId, LobbyRevision) WHERE State = 'Open'</c> is what makes a retried start
/// idempotent: the second attempt at the same revision loses at the index rather than creating a second request.
/// After a recorded recoverable failure, an explicit retry runs at a <em>new</em> revision and therefore mints a
/// new <see cref="MatchRequestId"/>.
/// </summary>
public class LobbyStartRequest : Entity
{
    public Guid LobbyId { get; private set; }

    /// <summary>The lobby revision this request was issued against. Part of the one-open-request-per-revision index.</summary>
    public int LobbyRevision { get; private set; }

    public Guid MatchRequestId { get; private set; }
    public LobbyStartRequestState State { get; private set; } = LobbyStartRequestState.Open;

    /// <summary>Caller-supplied idempotency key, so a client retry of the same command replays rather than re-runs.</summary>
    public string IdempotencyKey { get; private set; } = default!;

    public Guid CorrelationId { get; private set; }

    /// <summary>Set when M8 reports a recoverable failure; surfaced to the host verbatim-free (no internals).</summary>
    public string? FailureReason { get; private set; }

    public DateTime? ResolvedAtUtc { get; private set; }

    private LobbyStartRequest() { }

    public static LobbyStartRequest Open(
        Guid lobbyId,
        int lobbyRevision,
        Guid matchRequestId,
        string idempotencyKey,
        Guid correlationId)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey must not be empty.", nameof(idempotencyKey));
        if (matchRequestId == Guid.Empty)
            throw new ArgumentException("MatchRequestId must not be empty.", nameof(matchRequestId));

        return new LobbyStartRequest
        {
            LobbyId = lobbyId,
            LobbyRevision = lobbyRevision,
            MatchRequestId = matchRequestId,
            IdempotencyKey = idempotencyKey,
            CorrelationId = correlationId,
            State = LobbyStartRequestState.Open,
        };
    }

    public bool IsOpen => State == LobbyStartRequestState.Open;

    public LobbyOutcome MarkSucceeded(DateTime nowUtc)
    {
        if (!IsOpen) return LobbyOutcome.Closed;

        State = LobbyStartRequestState.Succeeded;
        ResolvedAtUtc = nowUtc;
        Touch();
        return LobbyOutcome.Ok;
    }

    public LobbyOutcome MarkFailed(string failureReason, DateTime nowUtc)
    {
        if (!IsOpen) return LobbyOutcome.Closed;

        State = LobbyStartRequestState.Failed;
        FailureReason = failureReason;
        ResolvedAtUtc = nowUtc;
        Touch();
        return LobbyOutcome.Ok;
    }
}
