using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.GameHost.Services;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Lobbies.Services;

/// <summary>
/// The lobby command surface. Every mutation runs through <see cref="ILobbyCommandRunner"/>, which re-runs the
/// whole read-decide-write delegate on contention (R3) — so the code below may be executed more than once per
/// request and must therefore make no decision it did not re-read.
/// </summary>
public sealed class LobbiesService : ILobbiesService
{
    private readonly ILobbyRepository _lobbies;
    private readonly ILobbyCommandRunner _runner;
    private readonly ILobbyCredentialHasher _hasher;
    private readonly ILobbyJoinThrottle _joinThrottle;
    private readonly IMatchRuntimeProbe _matchRuntime;
    private readonly IChatRuntimeProbe _chatRuntime;
    private readonly IAiParticipantProbe _aiParticipants;
    private readonly IGameRegistry _engines;
    private readonly IUserRepository _users;
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;
    private readonly LobbyCredentialOptions _credentialOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<LobbiesService> _logger;

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    /// <summary>
    /// Bounded retries when a freshly generated join code collides with a live one (23505 on the code digest
    /// index). At 60 bits of entropy against a handful of concurrently open lobbies, a single collision is already
    /// vanishingly unlikely and a second is not a thing that happens — but "vanishingly unlikely" is not "cannot",
    /// and an unbounded loop on a bug in the generator would spin forever.
    /// </summary>
    private const int MaxCredentialAttempts = 5;

    public LobbiesService(
        ILobbyRepository lobbies,
        ILobbyCommandRunner runner,
        ILobbyCredentialHasher hasher,
        ILobbyJoinThrottle joinThrottle,
        IMatchRuntimeProbe matchRuntime,
        IChatRuntimeProbe chatRuntime,
        IAiParticipantProbe aiParticipants,
        IGameRegistry engines,
        IUserRepository users,
        IFileStorageService storage,
        IOptions<StorageOptions> storageOptions,
        IOptions<LobbyCredentialOptions> credentialOptions,
        TimeProvider clock,
        ILogger<LobbiesService> logger)
    {
        _lobbies = lobbies;
        _runner = runner;
        _hasher = hasher;
        _joinThrottle = joinThrottle;
        _matchRuntime = matchRuntime;
        _chatRuntime = chatRuntime;
        _aiParticipants = aiParticipants;
        _engines = engines;
        _users = users;
        _storage = storage;
        _storageOptions = storageOptions.Value;
        _credentialOptions = credentialOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    // ── Create ───────────────────────────────────────────────────────────────

    public Task<Result<CreateLobbyResultDto>> CreateAsync(
        Guid actorUserId, CreateLobbyRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<CreateLobbyResultDto>(actorUserId, async token =>
        {
            var actor = await _users.GetByIdAsync(actorUserId, token);
            if (actor is null || actor.IsAccountSuspended())
                return Fail<CreateLobbyResultDto>(LobbyErrors.Forbidden, "Your account cannot host a lobby.");

            var settingsResult = BuildSettings(
                request.GameSlug, request.CapabilityVersion, request.Privacy, request.MaxPlayers,
                request.TimeControlId, request.Rated, request.Region, request.SpectatorPolicy,
                request.TieBreakRuleId, request.AiFillRequested, actor);
            if (!settingsResult.IsSuccess)
                return Result<CreateLobbyResultDto>.Fail(settingsResult.Error!);

            var settings = settingsResult.Value!;

            var capability = await ValidateCapabilityAsync(settings, token);
            if (!capability.IsSuccess)
                return Result<CreateLobbyResultDto>.Fail(capability.Error!);

            // Re-checked inside the transaction that inserts, not once up front — the state can change between a
            // pre-flight check and the write, and only the transaction sees the truth (brief Risk #2).
            var active = await EnsureNotAlreadyActiveAsync(actorUserId, token);
            if (!active.IsSuccess)
                return Result<CreateLobbyResultDto>.Fail(active.Error!);

            var nowUtc = NowUtc;
            var lobby = Lobby.Create(actorUserId, settings, correlationId: Guid.NewGuid(), nowUtc);

            var (credential, plaintext) = IssueCredential(lobby.Id, generation: 1, nowUtc);

            await _lobbies.AddLobbyAsync(
                lobby, credential,
                new[] { LobbyOutbox.LobbyCreatedEvent(lobby) },
                token);

            var dto = await ProjectAsync(lobby, actorUserId, token);
            return Result<CreateLobbyResultDto>.Ok(new CreateLobbyResultDto(dto, plaintext));
        }, ct);

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<Result<LobbyDto>> GetAsync(Guid actorUserId, Guid lobbyId, CancellationToken ct = default)
    {
        var lobby = await _lobbies.GetByIdAsync(lobbyId, ct);
        if (lobby is null) return NotFound<LobbyDto>();

        // Authorization, re-evaluated on every read against the object's current membership — never against a
        // token, a prior response, or the fact that the caller happens to hold the id.
        var isMember = lobby.FindJoinedMember(actorUserId) is not null;
        var isPubliclyVisible = lobby.Privacy == LobbyPrivacy.Public && lobby.State == LobbyState.Open;

        // A non-member looking at a private lobby gets the same 404 as a caller naming an id that never existed.
        if (!isMember && !isPubliclyVisible) return NotFound<LobbyDto>();

        return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, ct));
    }

    public async Task<Result<CursorPage<LobbySummaryDto>>> GetPublicAsync(
        Guid actorUserId, int limit, string? cursor, CancellationToken ct = default)
    {
        if (limit < 1 || limit > MaxLimit)
            return Fail<CursorPage<LobbySummaryDto>>(LobbyErrors.ValidationFailed, "Page size must be between 1 and 50.");

        DateTime? afterCreatedAt = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeTimeId(cursor, out var createdAt, out var id))
                return Fail<CursorPage<LobbySummaryDto>>(LobbyErrors.InvalidCursor, "The pagination cursor is invalid.");
            afterCreatedAt = createdAt;
            afterId = id;
        }

        var nowUtc = NowUtc;
        var rows = await _lobbies.GetPublicPageAsync(limit, afterCreatedAt, afterId, ct);

        // The keyset index cannot express "not expired", "not full", or "not blocked", so those three are applied
        // here. That means a page can come back shorter than `limit` — which is correct and deliberate: topping it
        // up would either leak that a hidden row existed (via the cursor skipping ahead) or require an unbounded
        // scan. The cursor still advances past every row the query saw, so pagination never duplicates or stalls.
        var hostIds = rows.Select(l => l.HostUserId).Distinct().ToList();
        var blockedHosts = await _lobbies.GetBlockedCounterpartsAsync(actorUserId, hostIds, ct);
        var blocked = blockedHosts.ToHashSet();

        var visible = rows
            .Where(l => !l.IsExpired(nowUtc))
            .Where(l => l.JoinedCount < l.MaxPlayers)
            .Where(l => !blocked.Contains(l.HostUserId))
            .ToList();

        var users = await _lobbies.GetUsersAsync(visible.Select(l => l.HostUserId).Distinct().ToList(), ct);

        var items = new List<LobbySummaryDto>(visible.Count);
        foreach (var lobby in visible)
        {
            if (!users.TryGetValue(lobby.HostUserId, out var host)) continue;
            items.Add(new LobbySummaryDto(
                lobby.Id, lobby.GameSlug, lobby.MaxPlayers, lobby.JoinedCount, lobby.TimeControlId,
                lobby.Rated, lobby.ResolvedRegion, lobby.SpectatorPolicy.ToString(),
                await ToIdentityAsync(host, ct), lobby.CreatedAt, lobby.ExpiresAtUtc));
        }

        // The cursor is derived from the last row the *query* returned, not the last visible one — otherwise a
        // trailing filtered-out row would be re-fetched forever and the page would never advance.
        var next = rows.Count == limit
            ? Cursor.EncodeTimeId(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return Result<CursorPage<LobbySummaryDto>>.Ok(new CursorPage<LobbySummaryDto>(items, next));
    }

    public async Task<Result<IReadOnlyList<LobbyInviteDto>>> GetMyInvitesAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await _lobbies.GetPendingInvitesForUserAsync(actorUserId, NowUtc, DefaultLimit, ct);

        var items = new List<LobbyInviteDto>(rows.Count);
        foreach (var (invite, lobby, inviter) in rows)
        {
            items.Add(new LobbyInviteDto(
                invite.Id, invite.LobbyId, lobby.GameSlug,
                await ToIdentityAsync(inviter, ct), invite.State.ToString(), invite.ExpiresAtUtc));
        }

        return Result<IReadOnlyList<LobbyInviteDto>>.Ok(items);
    }

    public async Task<Result<GameCapabilityProfileDto>> GetCapabilityProfileAsync(
        string gameSlug, CancellationToken ct = default)
    {
        var profile = await _lobbies.GetActiveCapabilityProfileAsync(gameSlug, ct);
        if (profile is null)
            return Fail<GameCapabilityProfileDto>(
                LobbyErrors.CapabilityNotFound, "That game has no active capability profile.");

        return Result<GameCapabilityProfileDto>.Ok(new GameCapabilityProfileDto(
            profile.GameSlug, profile.CapabilityVersion, profile.MinPlayers, profile.MaxPlayers,
            profile.AllowedModes, profile.TimeControls, profile.TieBreakRules, profile.SpectatorPolicies,
            profile.RatedEligible, profile.AiFillEligible));
    }

    public async Task<Result<ActiveContextDto>> GetMyActiveAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var lobby = await _lobbies.GetActiveLobbyForUserAsync(actorUserId, ct);
        if (lobby is not null)
            return Result<ActiveContextDto>.Ok(new ActiveContextDto(await ProjectAsync(lobby, actorUserId, ct), null));

        var ticketId = await _lobbies.GetActiveTicketIdForUserAsync(actorUserId, ct);
        return Result<ActiveContextDto>.Ok(new ActiveContextDto(null, ticketId));
    }

    // ── Join by credential ───────────────────────────────────────────────────

    public async Task<Result<LobbyDto>> JoinByCredentialAsync(
        Guid actorUserId, JoinLobbyRequestDto request, CancellationToken ct = default)
    {
        var hasCode = !string.IsNullOrWhiteSpace(request.Code);
        var hasToken = !string.IsNullOrWhiteSpace(request.LinkToken);
        var hasLobbyId = request.LobbyId is Guid id && id != Guid.Empty;

        var modeCount = (hasCode ? 1 : 0) + (hasToken ? 1 : 0) + (hasLobbyId ? 1 : 0);
        if (modeCount != 1)
            return Fail<LobbyDto>(LobbyErrors.ValidationFailed, "Supply exactly one of code, linkToken, or lobbyId.");

        // Joining by id skips the credential path entirely: a lobbyId is a resource identifier, not a secret, so
        // there is nothing here for the join throttle to protect against. It is only ever honored for a lobby that
        // is already Public and Open — the same visibility rule as the public browse listing and single-lobby
        // read — so naming a private, foreign, closed, or expired lobby's id answers with the identical
        // privacy-safe not-found used everywhere else in this module.
        if (hasLobbyId)
        {
            return await _runner.RunAsync<LobbyDto>(actorUserId, async token =>
            {
                var nowUtc = NowUtc;
                var lobby = await _lobbies.GetForUpdateAsync(request.LobbyId!.Value, token);
                if (lobby is null || lobby.Privacy != LobbyPrivacy.Public || lobby.IsTerminal || lobby.IsExpired(nowUtc))
                    return NotFound<LobbyDto>();

                return await SeatMemberAsync(lobby, actorUserId, nowUtc, extraEvents: null, token);
            }, ct);
        }

        // Failed attempts are throttled specifically (a wrong 60-bit code is cheap to spam), and the check runs
        // before any digest work so a throttled attacker cannot even measure the comparison.
        var retryAfter = await _joinThrottle.GetRetryAfterUtcAsync(actorUserId, ct);
        if (retryAfter is DateTime until)
        {
            return Result<LobbyDto>.Fail(new Error(
                LobbyErrors.RateLimitExceeded, "Too many failed join attempts. Please try again later.")
            {
                RetryAfterUtc = until,
            });
        }

        var result = await _runner.RunAsync<LobbyDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;

            var credential = hasCode
                ? await _lobbies.FindActiveByCodeDigestAsync(_hasher.HashCode(request.Code!), token)
                : await _lobbies.FindActiveByLinkTokenDigestAsync(_hasher.HashLinkToken(request.LinkToken!), token);

            // Wrong, expired, rotated, revoked, and unknown all land here, and all answer identically. A caller
            // learns only "that did not work" — never whether the lobby exists.
            if (credential is null || !credential.CanRedeem(nowUtc))
                return CredentialInvalid<LobbyDto>();

            var lobby = await _lobbies.GetForUpdateAsync(credential.LobbyId, token);
            if (lobby is null || lobby.IsTerminal || lobby.IsExpired(nowUtc))
                return CredentialInvalid<LobbyDto>();

            return await SeatMemberAsync(lobby, actorUserId, nowUtc, extraEvents: null, token);
        }, ct);

        // Only a genuinely invalid credential feeds the throttle. A caller rejected for being already-active, or
        // for landing in a full lobby, learned nothing about the code — counting those would let an unrelated
        // failure lock a legitimate user out of joining.
        if (!result.IsSuccess && result.Error!.Code == LobbyErrors.CredentialInvalid)
            await _joinThrottle.RecordFailureAsync(actorUserId, ct);
        else if (result.IsSuccess)
            await _joinThrottle.ClearAsync(actorUserId, ct);

        return result;
    }

    // ── Membership mutations ─────────────────────────────────────────────────

    public Task<Result> LeaveAsync(Guid actorUserId, Guid lobbyId, CancellationToken ct = default) =>
        Unit(_runner.RunAsync<bool>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null) return NotFound<bool>();

            // A caller who is not a seated member must not be able to tell a real lobby from a fictional one.
            if (lobby.FindJoinedMember(actorUserId) is null) return NotFound<bool>();

            var leave = lobby.Leave(actorUserId, nowUtc);
            if (leave.Outcome != LobbyOutcome.Ok) return MapOutcome<bool>(leave.Outcome);

            var events = new List<OutboxMessage> { LobbyOutbox.MemberLeftEvent(lobby, actorUserId) };
            if (leave.NewHostUserId is Guid newHost)
                events.Add(LobbyOutbox.HostTransferredEvent(lobby, newHost));
            if (leave.ClosedReason is not null)
                events.Add(LobbyOutbox.LobbyClosedEvent(lobby));

            await _lobbies.SaveAsync(events, token);
            return Result<bool>.Ok(true);
        }, ct));

    public Task<Result<LobbyDto>> SetReadinessAsync(
        Guid actorUserId, Guid lobbyId, SetReadinessRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null) return NotFound<LobbyDto>();

            var stale = CheckRevision(lobby, request.ExpectedRevision);
            if (stale is not null) return Result<LobbyDto>.Fail(stale);

            var outcome = lobby.SetReadiness(actorUserId, request.IsReady, nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<LobbyDto>(outcome);

            await _lobbies.SaveAsync(Array.Empty<OutboxMessage>(), token);
            return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, token));
        }, ct);

    public Task<Result<LobbyDto>> UpdateSettingsAsync(
        Guid actorUserId, Guid lobbyId, UpdateLobbySettingsRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null) return NotFound<LobbyDto>();

            var stale = CheckRevision(lobby, request.ExpectedRevision);
            if (stale is not null) return Result<LobbyDto>.Fail(stale);

            var actor = await _users.GetByIdAsync(actorUserId, token);
            if (actor is null) return NotFound<LobbyDto>();

            var settingsResult = BuildSettings(
                request.GameSlug, request.CapabilityVersion, request.Privacy, request.MaxPlayers,
                request.TimeControlId, request.Rated, request.Region, request.SpectatorPolicy,
                request.TieBreakRuleId, request.AiFillRequested, actor);
            if (!settingsResult.IsSuccess) return Result<LobbyDto>.Fail(settingsResult.Error!);

            // Capability is re-validated on every change, not just at create — a profile can be deactivated
            // underneath a live lobby, which is exactly what makes "capability disabled after create" a real path.
            var capability = await ValidateCapabilityAsync(settingsResult.Value!, token);
            if (!capability.IsSuccess) return Result<LobbyDto>.Fail(capability.Error!);

            var outcome = lobby.ChangeSettings(actorUserId, settingsResult.Value!, nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<LobbyDto>(outcome);

            await _lobbies.SaveAsync(new[] { LobbyOutbox.SettingsChangedEvent(lobby) }, token);
            return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, token));
        }, ct);

    public Task<Result<LobbyDto>> KickAsync(
        Guid actorUserId, Guid lobbyId, KickMemberRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null) return NotFound<LobbyDto>();

            var stale = CheckRevision(lobby, request.ExpectedRevision);
            if (stale is not null) return Result<LobbyDto>.Fail(stale);

            var outcome = lobby.Kick(actorUserId, request.TargetUserId, nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<LobbyDto>(outcome);

            await _lobbies.SaveAsync(
                new[] { LobbyOutbox.MemberKickedEvent(lobby, request.TargetUserId, actorUserId) }, token);
            return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, token));
        }, ct);

    // ── Credential rotation ──────────────────────────────────────────────────

    public Task<Result<LobbyCredentialDto>> RotateCredentialAsync(
        Guid actorUserId, Guid lobbyId, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyCredentialDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null)
                return NotFound<LobbyCredentialDto>();

            if (!lobby.IsHost(actorUserId))
                return Fail<LobbyCredentialDto>(LobbyErrors.Forbidden, "Only the host can rotate the join credential.");
            if (lobby.IsTerminal) return MapOutcome<LobbyCredentialDto>(LobbyOutcome.Closed);
            if (lobby.IsExpired(nowUtc)) return MapOutcome<LobbyCredentialDto>(LobbyOutcome.Expired);

            var outgoing = await _lobbies.GetActiveCredentialAsync(lobbyId, token);
            if (outgoing is null) return NotFound<LobbyCredentialDto>();

            outgoing.MarkRotated(nowUtc);
            var (incoming, plaintext) = IssueCredential(lobbyId, outgoing.Generation + 1, nowUtc);

            // The old value is dead the instant this commits — the rotated row can no longer be redeemed, and the
            // new row is a different secret entirely. There is no window in which both work.
            await _lobbies.RotateCredentialAsync(
                outgoing, incoming,
                new[] { LobbyOutbox.CredentialRotatedEvent(lobby, incoming.Generation) },
                token);

            return Result<LobbyCredentialDto>.Ok(plaintext);
        }, ct);

    // ── Invites ──────────────────────────────────────────────────────────────

    public Task<Result<LobbyInviteDto>> CreateInviteAsync(
        Guid actorUserId, Guid lobbyId, CreateInviteRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyInviteDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null) return NotFound<LobbyInviteDto>();

            if (!lobby.IsHost(actorUserId))
                return Fail<LobbyInviteDto>(LobbyErrors.Forbidden, "Only the host can invite.");
            if (lobby.IsTerminal) return MapOutcome<LobbyInviteDto>(LobbyOutcome.Closed);
            if (lobby.IsExpired(nowUtc)) return MapOutcome<LobbyInviteDto>(LobbyOutcome.Expired);

            if (request.InviteeUserId == actorUserId)
                return Fail<LobbyInviteDto>(LobbyErrors.InvalidTarget, "You cannot invite yourself.");

            var invitee = await _users.GetByIdAsync(request.InviteeUserId, token);
            if (invitee is null || invitee.IsAccountSuspended())
                return Fail<LobbyInviteDto>(LobbyErrors.InvalidTarget, "That player cannot be invited.");

            // Friends-only. This is what stops the invite endpoint being a way to reveal a private lobby (and its
            // existence) to an arbitrary user id.
            if (!await _lobbies.AreFriendsAsync(actorUserId, request.InviteeUserId, token))
                return Fail<LobbyInviteDto>(LobbyErrors.InvalidTarget, "You can only invite friends.");

            var blocked = await _lobbies.GetBlockedCounterpartsAsync(
                request.InviteeUserId, RosterOf(lobby), token);
            if (blocked.Count > 0)
                return Fail<LobbyInviteDto>(LobbyErrors.Blocked, "A block exists between that player and this lobby.");

            // Re-inviting someone who already holds a live invite replays it rather than stacking duplicates.
            var existing = await _lobbies.GetPendingInviteAsync(lobbyId, request.InviteeUserId, token);
            if (existing is not null && existing.CanAccept(nowUtc))
            {
                var inviterForExisting = await _users.GetByIdAsync(actorUserId, token);
                return Result<LobbyInviteDto>.Ok(new LobbyInviteDto(
                    existing.Id, lobbyId, lobby.GameSlug, await ToIdentityAsync(inviterForExisting!, token),
                    existing.State.ToString(), existing.ExpiresAtUtc));
            }

            var invite = LobbyInvite.Create(lobbyId, actorUserId, request.InviteeUserId, nowUtc);
            await _lobbies.AddInviteAsync(invite, new[] { LobbyOutbox.InviteCreatedEvent(invite) }, token);

            var inviter = await _users.GetByIdAsync(actorUserId, token);
            return Result<LobbyInviteDto>.Ok(new LobbyInviteDto(
                invite.Id, lobbyId, lobby.GameSlug, await ToIdentityAsync(inviter!, token),
                invite.State.ToString(), invite.ExpiresAtUtc));
        }, ct);

    public Task<Result> RevokeInviteAsync(
        Guid actorUserId, Guid lobbyId, Guid inviteId, CancellationToken ct = default) =>
        Unit(_runner.RunAsync<bool>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null) return NotFound<bool>();
            if (!lobby.IsHost(actorUserId))
                return Fail<bool>(LobbyErrors.Forbidden, "Only the host can revoke an invite.");

            var invite = await _lobbies.GetInviteForUpdateAsync(inviteId, token);
            // An invite id belonging to a different lobby is a 404, not a 403 — the caller must not learn it exists.
            if (invite is null || invite.LobbyId != lobbyId) return NotFound<bool>();

            var outcome = invite.Revoke(nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<bool>(outcome);

            await _lobbies.SaveAsync(new[] { LobbyOutbox.InviteRevokedEvent(invite) }, token);
            return Result<bool>.Ok(true);
        }, ct));

    public Task<Result<LobbyDto>> AcceptInviteAsync(
        Guid actorUserId, Guid inviteId, CancellationToken ct = default) =>
        _runner.RunAsync<LobbyDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var invite = await _lobbies.GetInviteForUpdateAsync(inviteId, token);

            // Another user's invite id is indistinguishable from one that never existed.
            if (invite is null || invite.InviteeUserId != actorUserId) return NotFound<LobbyDto>();

            if (!invite.CanAccept(nowUtc))
            {
                return invite.IsExpired(nowUtc)
                    ? MapOutcome<LobbyDto>(LobbyOutcome.Expired)
                    : MapOutcome<LobbyDto>(LobbyOutcome.Closed);
            }

            var lobby = await _lobbies.GetForUpdateAsync(invite.LobbyId, token);
            if (lobby is null) return NotFound<LobbyDto>();
            if (lobby.IsTerminal) return MapOutcome<LobbyDto>(LobbyOutcome.Closed);
            if (lobby.IsExpired(nowUtc)) return MapOutcome<LobbyDto>(LobbyOutcome.Expired);

            var accept = invite.Accept(nowUtc);
            if (accept != LobbyOutcome.Ok) return MapOutcome<LobbyDto>(accept);

            // Accepting seats the member under exactly the invariants a credential join enforces — an invite is a
            // permission to try, never a bypass of blocks, capacity, or one-active-lobby-or-ticket.
            return await SeatMemberAsync(
                lobby, actorUserId, nowUtc,
                extraEvents: new[] { LobbyOutbox.InviteAcceptedEvent(invite) },
                token);
        }, ct);

    public Task<Result> DeclineInviteAsync(Guid actorUserId, Guid inviteId, CancellationToken ct = default) =>
        Unit(_runner.RunAsync<bool>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var invite = await _lobbies.GetInviteForUpdateAsync(inviteId, token);
            if (invite is null || invite.InviteeUserId != actorUserId) return NotFound<bool>();

            // Declining is modelled as a revoke of the same pending invite: the lifecycle has one terminal
            // "withdrawn" state, and who withdrew it is already recorded by the actor on the event.
            var outcome = invite.Revoke(nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<bool>(outcome);

            await _lobbies.SaveAsync(new[] { LobbyOutbox.InviteRevokedEvent(invite) }, token);
            return Result<bool>.Ok(true);
        }, ct));

    // ── Start ────────────────────────────────────────────────────────────────

    public Task<Result<StartLobbyResultDto>> StartAsync(
        Guid actorUserId, Guid lobbyId, StartLobbyRequestDto request, CancellationToken ct = default) =>
        _runner.RunAsync<StartLobbyResultDto>(actorUserId, async token =>
        {
            var nowUtc = NowUtc;
            var lobby = await _lobbies.GetForUpdateAsync(lobbyId, token);
            if (lobby is null || lobby.FindJoinedMember(actorUserId) is null)
                return NotFound<StartLobbyResultDto>();

            if (!lobby.IsHost(actorUserId))
                return Fail<StartLobbyResultDto>(LobbyErrors.Forbidden, "Only the host can start the match.");

            // An already-committed start replays rather than minting a second request. Checked before the
            // revision guard: a client retrying the identical command must get its own result back, not a
            // stale-revision error caused by its own first attempt having bumped the revision.
            var replay = await _lobbies.GetStartRequestByIdempotencyKeyAsync(lobbyId, request.IdempotencyKey, token);
            if (replay is not null)
            {
                return Result<StartLobbyResultDto>.Ok(new StartLobbyResultDto(
                    lobbyId, replay.MatchRequestId, lobby.State.ToString(), lobby.Revision));
            }

            var stale = CheckRevision(lobby, request.ExpectedRevision);
            if (stale is not null) return Result<StartLobbyResultDto>.Fail(stale);

            // ── The M8 gate, checked FIRST (deliberate ordering; see below) ──
            //
            // The spec lists the engine (M5) check before the runtime (M8) check. With zero engines installed,
            // following that order literally would answer `Lobbies.CapabilityDisabled` — blaming *this lobby's
            // configuration* for what is actually a platform-wide absence of any match runtime at all. That is a
            // misleading error, and it contradicts the spec's own normative promise that "until M8 registers a
            // consumer, Start returns Lobbies.MatchRuntimeUnavailable". The honest answer wins.
            if (!await _matchRuntime.IsAvailableAsync(token))
            {
                _logger.LogInformation(
                    "Security: Start rejected, no match runtime. ActorId={ActorId} LobbyId={LobbyId} Action={Action} Result={Result}",
                    actorUserId, lobbyId, "LobbyStart", "MatchRuntimeUnavailable");
                return Fail<StartLobbyResultDto>(
                    LobbyErrors.MatchRuntimeUnavailable,
                    "Matches cannot start yet — the match runtime arrives with Module 8.");
            }

            var roster = RosterOf(lobby);

            // Blocks are re-checked at start, not trusted from join time: a block created while the lobby filled
            // must stop the match, and only a check here sees it.
            foreach (var member in roster)
            {
                var blocked = await _lobbies.GetBlockedCounterpartsAsync(member, roster, token);
                if (blocked.Count > 0)
                    return Fail<StartLobbyResultDto>(LobbyErrors.Blocked, "A block exists between two members of this lobby.");
            }

            foreach (var member in roster)
            {
                if (await _matchRuntime.IsInActiveMatchAsync(member, token))
                    return Fail<StartLobbyResultDto>(LobbyErrors.AlreadyActive, "A member is already in a live match.");
            }

            var capability = await ValidateCapabilityAsync(lobby.CurrentSettings, token);
            if (!capability.IsSuccess) return Result<StartLobbyResultDto>.Fail(capability.Error!);

            // M5 engine availability. The lobby pins a *capability* version, not an engine version — the two are
            // different concepts and M6 has no field for the latter. Until M8 exists to instantiate a specific
            // engine, the answerable question is "can any installed engine host this game at all", which is what
            // this asks. Selecting the exact (slug, engineVersion) is M8's, since M8 is what records it on the
            // match it creates.
            if (!_engines.RegisteredDefinitions.Any(d => string.Equals(d.Slug, lobby.GameSlug, StringComparison.Ordinal)))
            {
                return Fail<StartLobbyResultDto>(
                    LobbyErrors.CapabilityDisabled, "No engine is installed for this game.");
            }

            var outcome = lobby.BeginStarting(actorUserId, nowUtc);
            if (outcome != LobbyOutcome.Ok) return MapOutcome<StartLobbyResultDto>(outcome);

            // The state change and the event commit together or not at all. A committed MatchRequestedV1 is a
            // durable *request* — not a match (Risk #6).
            var startRequest = LobbyStartRequest.Open(
                lobbyId, lobby.Revision, matchRequestId: Guid.NewGuid(),
                request.IdempotencyKey, lobby.CorrelationId);

            await _lobbies.AddStartRequestAsync(
                startRequest,
                new[] { LobbyOutbox.MatchRequestedEvent(lobby, startRequest) },
                token);

            return Result<StartLobbyResultDto>.Ok(new StartLobbyResultDto(
                lobbyId, startRequest.MatchRequestId, lobby.State.ToString(), lobby.Revision));
        }, ct);

    public Task<Result<CreateLobbyResultDto>> CreateRematchLobbyAsync(
        Guid actorUserId, Guid terminalMatchId, CancellationToken ct = default)
    {
        // M8 owns matches. There is no match store to read a terminal match's pinned settings and participants
        // from, so there is nothing honest to build a rematch out of. Fabricating one from defaults would produce
        // a lobby that silently is not the rematch it claims to be.
        _logger.LogInformation(
            "Security: Rematch rejected, no match runtime. ActorId={ActorId} MatchId={MatchId} Action={Action} Result={Result}",
            actorUserId, terminalMatchId, "LobbyRematch", "MatchRuntimeUnavailable");

        return Task.FromResult(Fail<CreateLobbyResultDto>(
            LobbyErrors.MatchRuntimeUnavailable,
            "Rematch arrives with Module 8, which owns match records."));
    }

    // ── Shared command logic ─────────────────────────────────────────────────

    /// <summary>
    /// Seats an actor in a lobby they are entitled to reach. Shared verbatim by credential-join and invite-accept
    /// so the two can never drift apart on blocks, capacity, or the one-active-lobby-or-ticket rule.
    /// </summary>
    private async Task<Result<LobbyDto>> SeatMemberAsync(
        Lobby lobby, Guid actorUserId, DateTime nowUtc, IReadOnlyList<OutboxMessage>? extraEvents, CancellationToken ct)
    {
        // Already seated: idempotent, so a double-submitted join returns the lobby rather than an error.
        if (lobby.FindJoinedMember(actorUserId) is not null)
            return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, ct));

        var actor = await _users.GetByIdAsync(actorUserId, ct);
        if (actor is null || actor.IsAccountSuspended())
            return Fail<LobbyDto>(LobbyErrors.Forbidden, "Your account cannot join a lobby.");

        var active = await EnsureNotAlreadyActiveAsync(actorUserId, ct);
        if (!active.IsSuccess) return Result<LobbyDto>.Fail(active.Error!);

        var blocked = await _lobbies.GetBlockedCounterpartsAsync(actorUserId, RosterOf(lobby), ct);
        if (blocked.Count > 0)
            return Fail<LobbyDto>(LobbyErrors.Blocked, "A block exists between you and a member of this lobby.");

        if (await _matchRuntime.IsInActiveMatchAsync(actorUserId, ct))
            return Fail<LobbyDto>(LobbyErrors.AlreadyActive, "You are already in a live match.");

        var capability = await ValidateCapabilityAsync(lobby.CurrentSettings, ct);
        if (!capability.IsSuccess) return Result<LobbyDto>.Fail(capability.Error!);

        // Capacity as the aggregate sees it. This is the *optimistic* half: two racers can both pass it. What
        // actually decides the last seat is the lobby's xmin row version — both bump Revision, the loser's UPDATE
        // affects zero rows, and the command runner re-runs the whole delegate, which re-reads and lands on Full
        // below. That is why this method must never cache state across a retry.
        var outcome = lobby.Join(actorUserId, nowUtc);
        if (outcome != LobbyOutcome.Ok) return MapOutcome<LobbyDto>(outcome);

        var events = new List<OutboxMessage> { LobbyOutbox.MemberJoinedEvent(lobby, actorUserId) };
        if (extraEvents is not null) events.AddRange(extraEvents);

        await _lobbies.SaveAsync(events, ct);
        return Result<LobbyDto>.Ok(await ProjectAsync(lobby, actorUserId, ct));
    }

    /// <summary>
    /// The cross-table invariant: a user holds at most one joined lobby <strong>or</strong> one nonterminal
    /// ticket, never both and never two.
    ///
    /// Two filtered unique indexes cannot see each other, so this check is what covers the gap — and it is only
    /// sound because the command runner holds a transaction-scoped advisory lock on the actor, serializing their
    /// own seat-acquiring commands against each other (brief Risk #2). Without that lock a concurrent join and
    /// enqueue would both read "nothing active" and both commit.
    /// </summary>
    private async Task<Result> EnsureNotAlreadyActiveAsync(Guid userId, CancellationToken ct)
    {
        var lobby = await _lobbies.GetActiveLobbyForUserAsync(userId, ct);
        if (lobby is not null)
            return Result.Fail(LobbyErrors.AlreadyActive, "You are already in a lobby.");

        var ticketId = await _lobbies.GetActiveTicketIdForUserAsync(userId, ct);
        if (ticketId is not null)
            return Result.Fail(LobbyErrors.AlreadyActive, "You are already in the Quick Match queue.");

        return Result.Ok();
    }

    /// <summary>
    /// Validates settings against the pinned capability profile, M4's catalog, and the platform allow-lists —
    /// always <em>before</em> persistence, which is what turns "capability disabled after create" into a real,
    /// testable path rather than a lobby nobody can start.
    /// </summary>
    private async Task<Result> ValidateCapabilityAsync(LobbySettings settings, CancellationToken ct)
    {
        var profile = await _lobbies.GetCapabilityProfileAsync(settings.GameSlug, settings.CapabilityVersion, ct);
        if (profile is null)
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game/capability version is not available.");

        var permits = profile.Permits(settings);
        if (!permits.Allowed)
            return Result.Fail(LobbyErrors.CapabilityDisabled, permits.Reason!);

        var game = await _lobbies.GetGameAsync(settings.GameSlug, ct);
        if (game is null || game.Lifecycle != GameLifecycle.Available)
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game is not available to play right now.");

        // Drift between M6's profile and M4's catalog is a data bug, not a user error — but it fails closed all
        // the same, because a lobby it let through would be one M5's engine cannot host.
        var modes = await _lobbies.GetGameModesAsync(game.Id, ct);
        var drift = profile.ContradictsCatalog(game.MinPlayers, game.MaxPlayers, modes);
        if (!drift.Allowed)
        {
            _logger.LogWarning(
                "Capability profile drift for {GameSlug} v{CapabilityVersion}: {Reason}",
                settings.GameSlug, settings.CapabilityVersion, drift.Reason);
            return Result.Fail(LobbyErrors.CapabilityDisabled, "That game's configuration is temporarily unavailable.");
        }

        return Result.Ok();
    }

    /// <summary>
    /// Turns a request into validated domain settings. Every enum and allow-list value is parsed here, so an
    /// unparseable value is a 400 rather than an exception from deep inside the aggregate.
    /// </summary>
    private Result<LobbySettings> BuildSettings(
        string gameSlug, int capabilityVersion, string privacy, int maxPlayers, string timeControlId,
        bool rated, string? region, string spectatorPolicy, string tieBreakRuleId, bool aiFillRequested,
        User actor)
    {
        if (string.IsNullOrWhiteSpace(gameSlug))
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "gameSlug is required.");
        if (capabilityVersion < 1)
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "capabilityVersion must be at least 1.");
        if (maxPlayers < 2 || maxPlayers > 8)
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "maxPlayers must be between 2 and 8.");

        if (!Enum.TryParse<LobbyPrivacy>(privacy, ignoreCase: false, out var parsedPrivacy))
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "privacy must be Public or Private.");
        if (!Enum.TryParse<SpectatorPolicy>(spectatorPolicy, ignoreCase: false, out var parsedSpectators))
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "spectatorPolicy must be Anyone, FriendsOnly, or Disabled.");

        if (!LobbyAllowLists.TimeControls.Contains(timeControlId))
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "Unknown time control.");
        if (!LobbyAllowLists.TieBreakRules.Contains(tieBreakRuleId))
            return Fail<LobbySettings>(LobbyErrors.ValidationFailed, "Unknown tie-break rule.");

        // A client-supplied region that is not allow-listed is not an error — it falls through to the profile and
        // then the deployment default. Rejecting it would leak the region allow-list; silently partitioning the
        // matchmaking queue on it would be far worse.
        var resolvedRegion = LobbyRegion.Resolve(region, actor.Region, _credentialOptions.DefaultRegion);

        return Result<LobbySettings>.Ok(new LobbySettings(
            gameSlug, capabilityVersion, parsedPrivacy, maxPlayers, timeControlId, rated,
            resolvedRegion, parsedSpectators, tieBreakRuleId, aiFillRequested));
    }

    /// <summary>
    /// Mints a plaintext code + link token, hashes both, and returns the entity (digests only) alongside the
    /// plaintext the caller will hand back exactly once. The plaintext is never stored, logged, or evented.
    /// </summary>
    private (LobbyJoinCredential Entity, LobbyCredentialDto Plaintext) IssueCredential(
        Guid lobbyId, int generation, DateTime nowUtc)
    {
        var code = LobbyCredentialFormat.NewCode();
        var linkToken = LobbyCredentialFormat.NewLinkToken();

        var entity = LobbyJoinCredential.Issue(
            lobbyId, _hasher.HashCode(code), _hasher.HashLinkToken(linkToken), generation, nowUtc);

        return (entity, new LobbyCredentialDto(code, linkToken, generation, entity.ExpiresAtUtc));
    }

    // ── Projection ───────────────────────────────────────────────────────────

    private async Task<LobbyDto> ProjectAsync(Lobby lobby, Guid viewerId, CancellationToken ct)
    {
        var nowUtc = NowUtc;
        var roster = RosterOf(lobby);
        var users = await _lobbies.GetUsersAsync(roster, ct);

        var seats = new List<LobbySeatDto>(roster.Count);
        foreach (var member in lobby.JoinedMembers)
        {
            if (!users.TryGetValue(member.UserId, out var user)) continue;
            seats.Add(new LobbySeatDto(
                await ToIdentityAsync(user, ct),
                IsHost: lobby.IsHost(member.UserId),
                member.IsReady,
                member.JoinedAtUtc));
        }

        var readiness = new DependencyReadinessDto(
            Chat: await _chatRuntime.IsAvailableAsync(ct),
            MatchRuntime: await _matchRuntime.IsAvailableAsync(ct),
            AiParticipants: await _aiParticipants.IsAvailableAsync(ct));

        return new LobbyDto(
            lobby.Id, lobby.GameSlug, lobby.CapabilityVersion, lobby.Privacy.ToString(), lobby.MaxPlayers,
            lobby.TimeControlId, lobby.Rated, lobby.ResolvedRegion, lobby.SpectatorPolicy.ToString(),
            lobby.TieBreakRuleId, lobby.AiFillRequested, lobby.State.ToString(), lobby.Revision,
            lobby.ExpiresAtUtc, lobby.ClosedReason?.ToString(), lobby.HostUserId,
            seats,
            AllowedActionsFor(lobby, viewerId, readiness, nowUtc),
            readiness);
    }

    /// <summary>
    /// What this viewer may actually do right now. Derived server-side from the same state the commands enforce,
    /// so the client cannot offer a control the server will reject — and, critically, <c>start</c> is absent while
    /// no match runtime exists, which is what makes the disabled Start button honest rather than decorative.
    /// </summary>
    private static IReadOnlyList<string> AllowedActionsFor(
        Lobby lobby, Guid viewerId, DependencyReadinessDto readiness, DateTime nowUtc)
    {
        var actions = new List<string>();
        if (lobby.IsTerminal || lobby.IsExpired(nowUtc)) return actions;

        var member = lobby.FindJoinedMember(viewerId);
        if (member is null) return actions;

        actions.Add(LobbyActions.Leave);

        // The host is implicitly ready and cannot un-ready, so `ready` is not offered to them.
        if (!lobby.IsHost(viewerId)) actions.Add(LobbyActions.Ready);

        if (lobby.IsHost(viewerId))
        {
            actions.Add(LobbyActions.Settings);
            actions.Add(LobbyActions.Invite);
            actions.Add(LobbyActions.RotateCredential);
            if (lobby.JoinedCount > 1) actions.Add(LobbyActions.Kick);

            // Start appears only when it would actually succeed: the domain preconditions hold AND M8 exists.
            if (readiness.MatchRuntime && lobby.CanStart(nowUtc))
                actions.Add(LobbyActions.Start);
        }

        return actions;
    }

    private async Task<PublicIdentityDto> ToIdentityAsync(User user, CancellationToken ct) => new(
        user.Id, user.Username, user.DisplayName, user.Initials, user.Color,
        await BuildAvatarUrlAsync(user.AvatarObjectKey, user.AvatarUrl, ct),
        user.ProfileType.ToString());

    private async Task<string?> BuildAvatarUrlAsync(string? objectKey, string? fallbackUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            var expiry = TimeSpan.FromMinutes(_storageOptions.ReadUrlExpiryMinutes);
            return await _storage.CreatePresignedReadUrlAsync(objectKey, expiry, ct);
        }
        return fallbackUrl;
    }

    private static List<Guid> RosterOf(Lobby lobby) =>
        lobby.JoinedMembers.Select(m => m.UserId).ToList();

    // ── Result helpers ───────────────────────────────────────────────────────

    private static Error? CheckRevision(Lobby lobby, int expectedRevision) =>
        lobby.Revision == expectedRevision
            ? null
            : new Error(LobbyErrors.StaleRevision,
                $"The lobby has changed since you loaded it (current revision {lobby.Revision}).");

    /// <summary>
    /// Maps a domain outcome to the error catalogue. <see cref="LobbyOutcome.NotMember"/> becomes the
    /// <em>privacy-safe not-found</em>, never a 403 — a caller who is not a member must not learn the lobby exists.
    /// </summary>
    private static Result<T> MapOutcome<T>(LobbyOutcome outcome) => outcome switch
    {
        LobbyOutcome.Closed => Fail<T>(LobbyErrors.Closed, "This lobby is closed."),
        LobbyOutcome.Expired => Fail<T>(LobbyErrors.Expired, "This lobby has expired."),
        LobbyOutcome.Full => Fail<T>(LobbyErrors.Full, "This lobby is full."),
        LobbyOutcome.Forbidden => Fail<T>(LobbyErrors.Forbidden, "Only the host can do that."),
        LobbyOutcome.NotMember => NotFound<T>(),
        LobbyOutcome.AlreadyJoined => Fail<T>(LobbyErrors.AlreadyActive, "You are already in this lobby."),
        LobbyOutcome.InvalidTarget => Fail<T>(LobbyErrors.InvalidTarget, "That is not a valid target."),
        LobbyOutcome.NotStartable => Fail<T>(LobbyErrors.NotStartable, "This lobby cannot start yet."),
        _ => Fail<T>(LobbyErrors.ValidationFailed, "That action is not allowed."),
    };

    private static Result<T> NotFound<T>() =>
        Fail<T>(LobbyErrors.NotFound, "Lobby not found.");

    private static Result<T> CredentialInvalid<T>() =>
        Fail<T>(LobbyErrors.CredentialInvalid, "That join code or link is not valid.");

    private static Result<T> Fail<T>(string code, string message) =>
        Result<T>.Fail(code, message);

    private static async Task<Result> Unit(Task<Result<bool>> inner)
    {
        var result = await inner;
        return result.IsSuccess ? Result.Ok() : Result.Fail(result.Error!);
    }
}
