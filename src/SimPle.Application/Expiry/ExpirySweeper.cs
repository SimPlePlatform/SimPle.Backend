using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Expiry;

/// <summary>
/// The bounded expiry sweep. See <see cref="IExpirySweeper"/> for why it runs without Module 8 and why credentials
/// are not swept.
/// </summary>
public sealed class ExpirySweeper : IExpirySweeper
{
    private readonly IMatchmakingRepository _tickets;
    private readonly ILobbyRepository _lobbies;
    private readonly IWorkerTransaction _transaction;
    private readonly ExpiryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExpirySweeper> _logger;

    public ExpirySweeper(
        IMatchmakingRepository tickets,
        ILobbyRepository lobbies,
        IWorkerTransaction transaction,
        IOptions<ExpiryOptions> options,
        TimeProvider clock,
        ILogger<ExpirySweeper> logger)
    {
        _tickets = tickets;
        _lobbies = lobbies;
        _transaction = transaction;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public Task<ExpirySweepResult> SweepAsync(CancellationToken ct = default) =>
        _transaction.RunAsync(async token =>
        {
            var nowUtc = _clock.GetUtcNow().UtcDateTime;
            var batchSize = _options.BatchSize;

            var ticketRows = await _tickets.GetExpiredTicketsAsync(nowUtc, batchSize, token);
            var lobbyRows = await _lobbies.GetExpiredLobbiesAsync(nowUtc, batchSize, token);
            var inviteRows = await _lobbies.GetExpiredInvitesAsync(nowUtc, batchSize, token);

            var ticketsExpired = 0;
            var maxLag = TimeSpan.Zero;
            foreach (var ticket in ticketRows)
            {
                if (!ticket.TryTimeOut(nowUtc)) continue;   // idempotent: already terminal, or not actually due

                ticketsExpired++;

                // How late we were, per ticket. This is the signal — a sweep whose lag is creeping up is a sweep
                // that is about to start letting tickets outlive their own deadline visibly.
                var lag = nowUtc - ticket.DeadlineAtUtc;
                if (lag > maxLag) maxLag = lag;
            }

            var events = new List<OutboxMessage>();

            var lobbiesExpired = 0;
            foreach (var lobby in lobbyRows)
            {
                if (!lobby.TryExpire(nowUtc)) continue;

                lobbiesExpired++;

                // The close event carries the reason the aggregate recorded (Expired), so a consumer never has to
                // infer why a lobby ended from the absence of anything else.
                events.Add(LobbyOutbox.LobbyClosedEvent(lobby));
            }

            var invitesExpired = 0;
            foreach (var invite in inviteRows)
            {
                if (invite.TryExpire(nowUtc)) invitesExpired++;
            }

            // An expired invite emits no event: nobody acted, and M11 has no notification to send for "an invite you
            // ignored has quietly lapsed". The state change alone is the record.
            if (ticketsExpired + lobbiesExpired + invitesExpired > 0)
            {
                // One save covers tickets, lobbies, and invites: both repositories are scoped over the *same*
                // AppDbContext, so every entity read above is tracked by one change tracker and commits in one
                // transaction. Saving through each repository in turn would split the sweep into three commits and
                // let a crash leave a lobby expired but its invites still pending.
                await _tickets.SaveAsync(events, token);

                _logger.LogInformation(
                    "Expiry sweep. Tickets={Tickets} Lobbies={Lobbies} Invites={Invites} MaxTicketLagMs={LagMs}",
                    ticketsExpired, lobbiesExpired, invitesExpired, (long)maxLag.TotalMilliseconds);
            }

            return new ExpirySweepResult(ticketsExpired, lobbiesExpired, invitesExpired, maxLag);
        }, ct);
}
