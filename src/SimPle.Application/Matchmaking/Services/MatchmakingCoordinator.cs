using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.Outbox;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// One matching cycle: claim a bounded batch, form proposals, commit assignments and their single match request.
///
/// <para>
/// <strong>The cycle does not run while no match runtime is registered</strong>, and that gate is the most important
/// line in this file. A cycle that ran without Module 8 would mark tickets <c>Matched</c> and emit match requests
/// nobody consumes — the queue UI would show a found opponent and a room the player could never enter. That is
/// exactly the fabrication the brief forbids (Risk #6), so before M8 the queue honestly does nothing but widen and
/// time out. The code below is real, tested, and dormant; M8 replaces the probe and it turns on unchanged.
/// </para>
///
/// <para>
/// <strong><c>SKIP LOCKED</c> is not exclusivity</strong> (Risk #1). It stops two workers <em>contending</em> on one
/// row — it does not stop a requeued ticket or a serialization retry from attempting a second assignment. The
/// partial unique index <c>UNIQUE (TicketId) WHERE State = 'Active'</c> is what makes double assignment impossible;
/// when it fires, <see cref="IWorkerTransaction"/> re-runs the whole cycle, which re-reads and simply finds the
/// ticket already taken.
/// </para>
/// </summary>
public sealed class MatchmakingCoordinator : IMatchmakingCoordinator
{
    private readonly IMatchmakingRepository _tickets;
    private readonly IWorkerTransaction _transaction;
    private readonly IMatchRuntimeProbe _matchRuntime;
    private readonly MatchmakingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MatchmakingCoordinator> _logger;

    public MatchmakingCoordinator(
        IMatchmakingRepository tickets,
        IWorkerTransaction transaction,
        IMatchRuntimeProbe matchRuntime,
        IOptions<MatchmakingOptions> options,
        TimeProvider clock,
        ILogger<MatchmakingCoordinator> logger)
    {
        _tickets = tickets;
        _transaction = transaction;
        _matchRuntime = matchRuntime;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    public async Task<MatchmakingCycleResult> RunCycleAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("WorkerId must not be empty.", nameof(workerId));

        // The M8 gate. Queue *execution* is disabled without a match runtime; ticket create/status/cancel and the
        // expiry sweep stay fully functional, which is what lets a player queue, watch the band widen, and time out
        // honestly rather than be told a lie.
        if (!await _matchRuntime.IsAvailableAsync(ct))
        {
            var age = await _tickets.GetOldestQueuedAgeAsync(NowUtc, ct);
            return MatchmakingCycleResult.Disabled(age);
        }

        return await _transaction.RunAsync(async token =>
        {
            var nowUtc = NowUtc;

            var claimed = await _tickets.ClaimQueuedTicketsAsync(_options.BatchSize, nowUtc, token);
            if (claimed.Count == 0) return MatchmakingCycleResult.Idle;

            var candidates = await ExcludeUsersInLiveMatchesAsync(claimed, token);

            var userIds = candidates.Select(t => t.UserId).Distinct().ToList();
            var blocked = new BlockedPairs(await _tickets.GetBlockedPairsAsync(userIds, token));

            var proposals = MatchProposalBuilder.BuildProposals(candidates, nowUtc, blocked);

            var assignments = new List<MatchmakingAssignment>();
            var events = new List<OutboxMessage>();
            var matched = 0;

            foreach (var proposal in proposals)
            {
                // Guard before mutating anything: a proposal is committed whole or not at all, so a ticket that has
                // slipped out from under the builder (expired on the boundary, say) must not leave the rest of its
                // group half-claimed.
                if (!CanCommit(proposal, nowUtc)) continue;

                var groupId = Guid.NewGuid();
                var matchRequestId = Guid.NewGuid();

                foreach (var ticket in proposal.Tickets)
                {
                    ticket.Claim(workerId, nowUtc);
                    ticket.MarkMatched(nowUtc);
                    assignments.Add(MatchmakingAssignment.Create(ticket.Id, matchRequestId, groupId, nowUtc));
                    matched++;
                }

                // Exactly one event for the whole group — the group is what M8 creates a match from.
                events.Add(MatchmakingOutbox.MatchRequestedEvent(
                    groupId,
                    matchRequestId,
                    proposal.Anchor.GameSlug,
                    proposal.Anchor.CapabilityVersion,
                    proposal.Tickets.Select(t => t.Id).ToList()));
            }

            if (assignments.Count > 0)
            {
                // The state changes, the assignments, and the events commit together or not at all.
                await _tickets.AddAssignmentsAsync(assignments, events, token);

                _logger.LogInformation(
                    "Matchmaking cycle matched tickets. Worker={Worker} Claimed={Claimed} Proposals={Proposals} Matched={Matched}",
                    workerId, claimed.Count, events.Count, matched);
            }

            var oldest = await _tickets.GetOldestQueuedAgeAsync(nowUtc, token);

            return new MatchmakingCycleResult(
                Executed: true,
                TicketsClaimed: claimed.Count,
                ProposalsFormed: events.Count,
                TicketsMatched: matched,
                OldestQueuedAge: oldest);
        }, ct);
    }

    /// <summary>
    /// The cross-cutting active-participation rule, re-asked at assignment.
    ///
    /// It is checked here as well as at enqueue on purpose: a player can enter a live match in the sixty seconds
    /// their ticket is queued, and a single up-front check would happily assign them to a second one. With no
    /// runtime this is trivially a no-op — which is a true answer, not a stub: a user genuinely cannot be in a live
    /// match when no match can exist.
    /// </summary>
    private async Task<IReadOnlyList<MatchmakingTicket>> ExcludeUsersInLiveMatchesAsync(
        IReadOnlyList<MatchmakingTicket> tickets, CancellationToken ct)
    {
        var eligible = new List<MatchmakingTicket>(tickets.Count);

        foreach (var ticket in tickets)
        {
            if (await _matchRuntime.IsInActiveMatchAsync(ticket.UserId, ct))
            {
                // Left Queued deliberately. The player is busy, not wrong: their ticket stays in the queue and is
                // reconsidered next cycle, and if they are still in a match when it runs out, it times out honestly.
                _logger.LogInformation(
                    "Matchmaking skipped a ticket whose owner is in a live match. TicketId={TicketId}", ticket.Id);
                continue;
            }

            eligible.Add(ticket);
        }

        return eligible;
    }

    private static bool CanCommit(MatchProposal proposal, DateTime nowUtc) =>
        proposal.Tickets.All(t => t.State == MatchmakingTicketState.Queued && !t.IsExpired(nowUtc));
}
