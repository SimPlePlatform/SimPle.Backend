using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.DTOs;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;
using SimPle.Shared.Common;

namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// The Quick Match ticket command surface.
///
/// Enqueue and cancel run through <see cref="ILobbyCommandRunner"/> — the <em>same</em> runner the lobby commands
/// use, and deliberately so. It holds a transaction-scoped advisory lock keyed on the actor, which is the only
/// thing that makes the cross-table "one active lobby <strong>or</strong> one active ticket" invariant real: the
/// filtered unique index on <c>lobby_members</c> and the one on <c>matchmaking_tickets</c> live on different tables
/// and cannot see each other, so without serializing an actor's seat-acquiring commands against each other, a
/// concurrent join and enqueue would both read "nothing active" and both commit (brief Risk #2).
/// </summary>
public sealed class MatchmakingService : IMatchmakingService
{
    private readonly IMatchmakingRepository _tickets;
    private readonly ILobbyRepository _lobbies;
    private readonly ILobbyCommandRunner _runner;
    private readonly IMatchRuntimeProbe _matchRuntime;
    private readonly IChatRuntimeProbe _chatRuntime;
    private readonly IAiParticipantProbe _aiParticipants;
    private readonly IUserRepository _users;
    private readonly LobbyCredentialOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MatchmakingService> _logger;

    public MatchmakingService(
        IMatchmakingRepository tickets,
        ILobbyRepository lobbies,
        ILobbyCommandRunner runner,
        IMatchRuntimeProbe matchRuntime,
        IChatRuntimeProbe chatRuntime,
        IAiParticipantProbe aiParticipants,
        IUserRepository users,
        IOptions<LobbyCredentialOptions> options,
        TimeProvider clock,
        ILogger<MatchmakingService> logger)
    {
        _tickets = tickets;
        _lobbies = lobbies;
        _runner = runner;
        _matchRuntime = matchRuntime;
        _chatRuntime = chatRuntime;
        _aiParticipants = aiParticipants;
        _users = users;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    // ── Enqueue ──────────────────────────────────────────────────────────────

    public Task<Result<TicketDto>> EnqueueAsync(
        Guid actorUserId, CreateTicketRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<TicketDto>(actorUserId, async token =>
        {
            var actor = await _users.GetByIdAsync(actorUserId, token);
            if (actor is null || actor.IsAccountSuspended())
                return Fail<TicketDto>(LobbyErrors.Forbidden, "Your account cannot join the Quick Match queue.");

            var validation = ValidateRequest(request);
            if (!validation.IsSuccess) return Result<TicketDto>.Fail(validation.Error!);

            var resolvedRegion = LobbyRegion.Resolve(request.Region, actor.Region, _options.DefaultRegion);

            // Idempotency without a key: an identical live ticket *is* the caller's previous attempt. This is the
            // same shape a double-submitted join gets (SeatMemberAsync returns the lobby rather than an error), and
            // it is why a flaky network cannot leave a user unable to queue because "they are already queued".
            var existing = await _tickets.GetActiveTicketForUserAsync(actorUserId, token);
            if (existing is not null)
            {
                if (IsSameTicket(existing, request, resolvedRegion))
                    return Result<TicketDto>.Ok(await ProjectAsync(existing, token));

                return Fail<TicketDto>(
                    MatchmakingErrors.AlreadyQueued, "You are already in the Quick Match queue.");
            }

            // The other half of the cross-table invariant. Checked inside the transaction that inserts, under the
            // runner's advisory lock — a pre-flight check outside it would be a race, not a guarantee.
            var activeLobby = await _lobbies.GetActiveLobbyForUserAsync(actorUserId, token);
            if (activeLobby is not null)
                return Fail<TicketDto>(LobbyErrors.AlreadyActive, "You are already in a lobby.");

            // Re-asked here, not trusted from an earlier step: a user can enter a live match between two requests.
            if (await _matchRuntime.IsInActiveMatchAsync(actorUserId, token))
                return Fail<TicketDto>(LobbyErrors.AlreadyActive, "You are already in a live match.");

            var capability = await ValidateCapabilityAsync(request, token);
            if (!capability.IsSuccess) return Result<TicketDto>.Fail(capability.Error!);

            var ticket = MatchmakingTicket.Enqueue(
                actorUserId,
                request.GameSlug,
                request.CapabilityVersion,
                request.Mode,
                request.PlayerCount,
                request.TimeControlId,
                request.Rated,
                resolvedRegion,
                // Provisional until M10. The legacy global User.Elo column is deliberately NOT substituted: it is a
                // single cross-game number, so presenting it as a per-game rating would be a fabricated signal — and
                // one that silently decides who people play against.
                MatchmakingTicket.ProvisionalRating,
                MatchmakingTicket.ProvisionalRatingSource,
                correlationId: Guid.NewGuid(),
                NowUtc);

            await _tickets.AddTicketAsync(ticket, token);

            return Result<TicketDto>.Ok(await ProjectAsync(ticket, token));
        }, ct);

    // ── Status ───────────────────────────────────────────────────────────────

    public async Task<Result<TicketDto>> GetTicketAsync(
        Guid actorUserId, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await _tickets.GetTicketAsync(ticketId, ct);

        // Another user's ticket id is indistinguishable from one that never existed. A 403 here would confirm it
        // exists, which is exactly the BOLA leak (OWASP API1:2023) the 404 closes.
        if (ticket is null || ticket.UserId != actorUserId) return TicketNotFound<TicketDto>();

        return Result<TicketDto>.Ok(await ProjectAsync(ticket, ct));
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    public Task<Result<TicketDto>> CancelAsync(
        Guid actorUserId, Guid ticketId, CancellationToken ct = default) =>
        _runner.RunAsync<TicketDto>(actorUserId, async token =>
        {
            var ticket = await _tickets.GetTicketForUpdateAsync(ticketId, token);
            if (ticket is null || ticket.UserId != actorUserId) return TicketNotFound<TicketDto>();

            var outcome = ticket.Cancel(NowUtc);

            // A cancel that lost to a worker claim is not an error and must not be reported as one — the user
            // pressed cancel in good faith and the queue simply got there first. They get a 200 and the ticket's
            // real current state, which is what the polling UI needs anyway.
            if (outcome is MatchmakingOutcome.AlreadyClaimed or MatchmakingOutcome.Terminal)
                return Result<TicketDto>.Ok(await ProjectAsync(ticket, token));

            if (outcome != MatchmakingOutcome.Ok) return MapOutcome<TicketDto>(outcome);

            await _tickets.SaveAsync(Array.Empty<OutboxMessage>(), token);
            return Result<TicketDto>.Ok(await ProjectAsync(ticket, token));
        }, ct);

    // ── Validation ───────────────────────────────────────────────────────────

    private static Result ValidateRequest(CreateTicketRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.GameSlug))
            return Result.Fail(LobbyErrors.ValidationFailed, "gameSlug is required.");
        if (request.CapabilityVersion < 1)
            return Result.Fail(LobbyErrors.ValidationFailed, "capabilityVersion must be at least 1.");
        if (string.IsNullOrWhiteSpace(request.Mode))
            return Result.Fail(LobbyErrors.ValidationFailed, "mode is required.");
        if (!GameCatalogAllowLists.Modes.Contains(request.Mode))
            return Result.Fail(LobbyErrors.ValidationFailed, "Unknown mode.");

        // Quick Match is a multiplayer queue by definition — a one-player "match" has nobody to find.
        if (request.PlayerCount < 2 || request.PlayerCount > 8)
            return Result.Fail(LobbyErrors.ValidationFailed, "playerCount must be between 2 and 8.");

        if (!LobbyAllowLists.TimeControls.Contains(request.TimeControlId))
            return Result.Fail(LobbyErrors.ValidationFailed, "Unknown time control.");

        return Result.Ok();
    }

    /// <summary>
    /// Validates the ticket against its pinned capability profile, M4's catalog, and the platform allow-lists —
    /// before persistence, so a ticket that could never be satisfied is never created.
    /// </summary>
    private async Task<Result> ValidateCapabilityAsync(CreateTicketRequestDto request, CancellationToken ct)
    {
        var profile = await _lobbies.GetCapabilityProfileAsync(request.GameSlug, request.CapabilityVersion, ct);
        if (profile is null)
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game/capability version is not available.");

        var permits = profile.PermitsTicket(
            request.GameSlug, request.CapabilityVersion, request.Mode,
            request.PlayerCount, request.TimeControlId, request.Rated);
        if (!permits.Allowed)
            return Result.Fail(LobbyErrors.CapabilityDisabled, permits.Reason!);

        var game = await _lobbies.GetGameAsync(request.GameSlug, ct);
        if (game is null || game.Lifecycle != GameLifecycle.Available)
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game is not available to play right now.");

        // Drift between M6's profile and M4's catalog is a data bug, not a user error — but it fails closed all the
        // same, because a ticket it let through would produce a match M5's engine cannot host.
        var modes = await _lobbies.GetGameModesAsync(game.Id, ct);
        var drift = profile.ContradictsCatalog(game.MinPlayers, game.MaxPlayers, modes);
        if (!drift.Allowed)
        {
            _logger.LogWarning(
                "Capability profile drift for {GameSlug} v{CapabilityVersion}: {Reason}",
                request.GameSlug, request.CapabilityVersion, drift.Reason);
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game's configuration is temporarily unavailable.");
        }

        return Result.Ok();
    }

    /// <summary>The full candidate-pool key. Anything less would treat two genuinely different searches as one.</summary>
    private static bool IsSameTicket(MatchmakingTicket ticket, CreateTicketRequestDto request, string resolvedRegion) =>
        string.Equals(ticket.GameSlug, request.GameSlug, StringComparison.Ordinal)
        && ticket.CapabilityVersion == request.CapabilityVersion
        && string.Equals(ticket.Mode, request.Mode, StringComparison.Ordinal)
        && ticket.PlayerCount == request.PlayerCount
        && string.Equals(ticket.TimeControlId, request.TimeControlId, StringComparison.Ordinal)
        && ticket.Rated == request.Rated
        && string.Equals(ticket.ResolvedRegion, resolvedRegion, StringComparison.Ordinal);

    // ── Projection ───────────────────────────────────────────────────────────

    private async Task<TicketDto> ProjectAsync(MatchmakingTicket ticket, CancellationToken ct)
    {
        var nowUtc = NowUtc;

        // Only a matched ticket has a handoff to point at. A MatchRequestId is a durable *request* — the client may
        // not navigate to a room on the strength of it, and dependencyReadiness.MatchRuntime is what says so.
        TicketAssignmentDto? assignment = null;
        if (ticket.State == MatchmakingTicketState.Matched)
        {
            var row = await _tickets.GetActiveAssignmentAsync(ticket.Id, ct);
            if (row is not null)
                assignment = new TicketAssignmentDto(row.MatchRequestId, row.GroupId);
        }

        var readiness = new DependencyReadinessDto(
            Chat: await _chatRuntime.IsAvailableAsync(ct),
            MatchRuntime: await _matchRuntime.IsAvailableAsync(ct),
            AiParticipants: await _aiParticipants.IsAvailableAsync(ct));

        return new TicketDto(
            ticket.Id, ticket.GameSlug, ticket.CapabilityVersion, ticket.Mode, ticket.PlayerCount,
            ticket.TimeControlId, ticket.Rated, ticket.ResolvedRegion, ticket.Rating, ticket.RatingSourceVersion,
            ticket.State.ToString(), ticket.EnqueuedAtUtc, ticket.DeadlineAtUtc,
            ticket.CurrentBand(nowUtc), assignment, readiness);
    }

    // ── Result helpers ───────────────────────────────────────────────────────

    private static Result<T> MapOutcome<T>(MatchmakingOutcome outcome) => outcome switch
    {
        MatchmakingOutcome.Expired => Fail<T>(MatchmakingErrors.TicketExpired, "This ticket has expired."),
        MatchmakingOutcome.Terminal => Fail<T>(MatchmakingErrors.TicketExpired, "This ticket is no longer active."),
        _ => Fail<T>(LobbyErrors.ValidationFailed, "That action is not allowed."),
    };

    private static Result<T> TicketNotFound<T>() =>
        Fail<T>(MatchmakingErrors.TicketNotFound, "Ticket not found.");

    private static Result<T> Fail<T>(string code, string message) => Result<T>.Fail(code, message);
}
