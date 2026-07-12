using SimPle.Application.Matchmaking.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// The Quick Match ticket surface (slice 6C). The matching worker, the expiry sweep, and the outbox dispatcher are
/// separate — a user never drives them, and they never run inside a request.
///
/// Every method takes the actor as its first argument and it is always the JWT <c>sub</c> claim. A ticket id
/// belonging to somebody else is a privacy-safe not-found, never a 403.
/// </summary>
public interface IMatchmakingService
{
    /// <summary>
    /// Enqueue. Idempotent by pool key: re-sending the identical ticket returns the live one rather than minting a
    /// second (the ticket entity has no idempotency-key column by design — see <see cref="CreateTicketRequestDto"/>).
    /// A <em>different</em> ticket while one is live is <c>Matchmaking.AlreadyQueued</c>.
    ///
    /// Succeeds even though no match runtime exists: the spec requires create/status/cancel and expiry to stay
    /// fully functional before M8. The returned ticket's <c>dependencyReadiness</c> is what tells the truth about
    /// what will happen next.
    /// </summary>
    Task<Result<TicketDto>> EnqueueAsync(
        Guid actorUserId, CreateTicketRequestDto request, CancellationToken ct = default);

    /// <summary>Polled every 2 seconds by the queue modal until M7 supplies live delivery.</summary>
    Task<Result<TicketDto>> GetTicketAsync(Guid actorUserId, Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// Cancel. Commits only while <c>Queued</c>; a cancel that arrives after a worker claim is <strong>not an
    /// error</strong> — it returns the ticket's current status with a 200, because the user did nothing wrong and
    /// the queue genuinely did get there first.
    /// </summary>
    Task<Result<TicketDto>> CancelAsync(Guid actorUserId, Guid ticketId, CancellationToken ct = default);
}
