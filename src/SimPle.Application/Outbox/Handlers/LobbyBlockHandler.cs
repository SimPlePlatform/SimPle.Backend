using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Friends.Outbox;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Outbox.Handlers;

/// <summary>
/// Applies a new Module 3 block to any lobby the two users currently share (<strong>D3</strong>).
///
/// <para>
/// The rule, from the brief: on a new block in an open lobby, a non-host blocker <em>leaves</em>; a host blocker
/// <em>removes</em> the blocked member. Asymmetric on purpose — a host who blocks someone should not be evicted
/// from the lobby they own, and a member who blocks the host has no authority to remove them, so the only thing
/// they can do is leave.
/// </para>
///
/// <para>
/// <strong>Idempotent by construction, not by bookkeeping.</strong> It decides from the users' <em>current</em>
/// lobby membership, never from the event's age or its own delivery history. A duplicate delivery finds the pair
/// already separated and does nothing; a historical <c>UserBlockedV1</c> replayed on a fresh deployment finds no
/// shared lobby and does nothing. That is why this handler needs no activation watermark — the same property that
/// makes at-least-once delivery safe also makes a backfill safe, and a watermark would have been a second
/// mechanism to get wrong.
/// </para>
///
/// <para>
/// It runs through <see cref="ILobbyCommandRunner"/> like every other lobby mutation, so it takes the same advisory
/// lock and the same bounded whole-command retry. A block landing at the same moment as a join is contention, not a
/// special case.
/// </para>
/// </summary>
public sealed class LobbyBlockHandler : IOutboxHandler
{
    private readonly ILobbyRepository _lobbies;
    private readonly ILobbyCommandRunner _runner;
    private readonly TimeProvider _clock;
    private readonly ILogger<LobbyBlockHandler> _logger;

    public LobbyBlockHandler(
        ILobbyRepository lobbies,
        ILobbyCommandRunner runner,
        TimeProvider clock,
        ILogger<LobbyBlockHandler> logger)
    {
        _lobbies = lobbies;
        _runner = runner;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Persisted in every delivery row. Renaming it replays the entire block history — see the interface.</summary>
    public string HandlerName => "lobby-block";

    public IReadOnlyList<string> EventTypes { get; } = new[] { FriendOutbox.UserBlocked };

    /// <summary>
    /// <c>FriendOutbox</c> serializes its payloads from anonymous objects, so the JSON is camelCase
    /// (<c>blockerId</c>) while this record's properties are PascalCase. System.Text.Json is <strong>case-sensitive
    /// by default</strong>: without this, every id would deserialize to <c>Guid.Empty</c> and the handler would
    /// quietly treat every block as unreadable — a bug with no exception and no failing write to notice it by.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<BlockPayload>(message.Payload, PayloadOptions);
        if (payload is null || payload.BlockerId == Guid.Empty || payload.BlockedId == Guid.Empty)
        {
            // A malformed payload is not retryable — replaying it will produce the same nothing. Swallowing it here
            // (rather than throwing) lets the dispatcher mark it processed instead of burning the retry budget and
            // dead-lettering a row that no amount of retrying can fix.
            _logger.LogWarning(
                "Outbox: UserBlockedV1 payload could not be read. EventId={EventId}", message.Id);
            return;
        }

        var result = await _runner.RunAsync<bool>(payload.BlockerId, async token =>
        {
            var nowUtc = _clock.GetUtcNow().UtcDateTime;

            // The blocker's lobby is the only one that can contain both of them: a user has at most one joined
            // nonterminal lobby, which is the invariant the whole module is built on.
            var lobby = await _lobbies.GetActiveLobbyForUserAsync(payload.BlockerId, token);
            if (lobby is null) return Ok(false);

            // Re-read tracked; the query above is untracked (it renders responses elsewhere).
            var tracked = await _lobbies.GetForUpdateAsync(lobby.Id, token);
            if (tracked is null || tracked.IsTerminal || tracked.IsExpired(nowUtc)) return Ok(false);

            if (tracked.FindJoinedMember(payload.BlockerId) is null) return Ok(false);
            if (tracked.FindJoinedMember(payload.BlockedId) is null) return Ok(false);

            var events = new List<OutboxMessage>();

            if (tracked.IsHost(payload.BlockerId))
            {
                var kick = tracked.Kick(payload.BlockerId, payload.BlockedId, nowUtc);
                if (kick != LobbyOutcome.Ok) return Ok(false);

                events.Add(LobbyOutbox.MemberKickedEvent(tracked, payload.BlockedId, payload.BlockerId));
            }
            else
            {
                var leave = tracked.Leave(payload.BlockerId, nowUtc);
                if (leave.Outcome != LobbyOutcome.Ok) return Ok(false);

                events.Add(LobbyOutbox.MemberLeftEvent(tracked, payload.BlockerId));

                // A blocker who happened to be the last eligible human still triggers the ordinary host-transfer /
                // close rules — a block is not a special exit path, it is an exit.
                if (leave.NewHostUserId is Guid newHost)
                    events.Add(LobbyOutbox.HostTransferredEvent(tracked, newHost));
                if (leave.ClosedReason is not null)
                    events.Add(LobbyOutbox.LobbyClosedEvent(tracked));
            }

            await _lobbies.SaveAsync(events, token);
            return Ok(true);
        }, ct);

        // A typed failure from the runner (a spent retry budget) is a real failure: throwing hands it back to the
        // dispatcher, which will retry the delivery on a later cycle rather than silently dropping the block.
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Applying a block to its shared lobby failed: {result.Error!.Code}");
        }

        if (result.Value)
        {
            _logger.LogInformation(
                "Security: Block applied to a shared lobby. Action={Action} Result={Result} EventId={EventId}",
                "LobbyBlockEnforcement", "Separated", message.Id);
        }
    }

    private static Shared.Common.Result<bool> Ok(bool value) => Shared.Common.Result<bool>.Ok(value);

    /// <summary>
    /// Matches <c>FriendOutbox.BlockEvent</c>'s payload. Ids only — the block reason is deliberately not in the
    /// event, and this handler has no use for one.
    /// </summary>
    private sealed record BlockPayload(Guid BlockId, Guid BlockerId, Guid BlockedId);
}
