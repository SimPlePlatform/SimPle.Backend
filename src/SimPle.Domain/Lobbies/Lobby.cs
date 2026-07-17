using SimPle.Domain.Common;

namespace SimPle.Domain.Lobbies;

/// <summary>
/// A lobby and its seats. Replaces the orphaned pre-module stub (R2), whose <c>Open|Closed|InGame</c> status could
/// not express this lifecycle and whose plaintext code violated the keyed-digest rule.
///
/// This aggregate owns the three invariants the brief flags as easy to get wrong:
/// deterministic host transfer (Risk #3), readiness-reset scope (Risk #4), and terminal-state rejection.
/// It does <em>not</em> validate settings against a game's capability profile — that cross-aggregate check needs
/// M4's catalog and M5's engine registry, so it lives in the 6B service layer, which calls
/// <c>GameCapabilityProfile.Permits(...)</c> before ever reaching this type.
///
/// Every time-sensitive value is passed in as an explicit <c>nowUtc</c> from the caller's injected
/// <c>TimeProvider</c> (R4). The aggregate never calls <c>DateTime.UtcNow</c> — that is what makes the mandatory
/// fake-clock expiry tests (Risk #8) possible.
/// </summary>
public class Lobby : Entity
{
    /// <summary>An open lobby expires two hours after creation. Fixed policy, not configurable.</summary>
    public static readonly TimeSpan OpenLifetime = TimeSpan.FromHours(2);

    private readonly List<LobbyMember> _members = new();

    public string GameSlug { get; private set; } = default!;
    public int CapabilityVersion { get; private set; }
    public Guid HostUserId { get; private set; }
    public LobbyPrivacy Privacy { get; private set; }
    public int MaxPlayers { get; private set; }
    public string TimeControlId { get; private set; } = default!;
    public bool Rated { get; private set; }
    public string ResolvedRegion { get; private set; } = default!;
    public SpectatorPolicy SpectatorPolicy { get; private set; }
    public string TieBreakRuleId { get; private set; } = default!;

    /// <summary>
    /// Stored and displayed, but cannot create an AI participant before M9. Ranked start is disabled while it is
    /// set — see <see cref="CanStart"/>.
    /// </summary>
    public bool AiFillRequested { get; private set; }

    public LobbyState State { get; private set; } = LobbyState.Open;

    /// <summary>
    /// Bumped on every mutation. Clients send it back as an expected revision; a mismatch is a typed
    /// <c>Lobbies.StaleRevision</c> conflict, never a 500. Starts at 1 (the as-created state).
    /// </summary>
    public int Revision { get; private set; } = 1;

    public DateTime ExpiresAtUtc { get; private set; }
    public LobbyClosedReason? ClosedReason { get; private set; }
    public Guid CorrelationId { get; private set; }

    /// <summary>Mapped to xmin via IsRowVersion() in EF config (the Npgsql optimistic-concurrency pattern).</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<LobbyMember> Members => _members;

    /// <summary>Members currently holding a seat, oldest-tenured first — the host-transfer order.</summary>
    public IEnumerable<LobbyMember> JoinedMembers =>
        _members.Where(m => m.IsJoined).OrderBy(m => m.JoinedAtUtc).ThenBy(m => m.UserId);

    public int JoinedCount => _members.Count(m => m.IsJoined);

    public bool IsTerminal =>
        State is LobbyState.Started or LobbyState.Closed or LobbyState.Expired;

    private Lobby() { }

    public static Lobby Create(
        Guid hostUserId,
        LobbySettings settings,
        Guid correlationId,
        DateTime nowUtc)
    {
        if (hostUserId == Guid.Empty)
            throw new ArgumentException("HostUserId must not be empty.", nameof(hostUserId));

        var lobby = new Lobby
        {
            HostUserId = hostUserId,
            CorrelationId = correlationId,
            State = LobbyState.Open,
            Revision = 1,
            ExpiresAtUtc = nowUtc + OpenLifetime,
        };

        lobby.ApplySettings(settings);

        // The host occupies the first seat and is implicitly ready.
        lobby._members.Add(LobbyMember.Join(lobby.Id, hostUserId, nowUtc, isReady: true));

        return lobby;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    public LobbyMember? FindJoinedMember(Guid userId) =>
        _members.FirstOrDefault(m => m.UserId == userId && m.IsJoined);

    public bool IsHost(Guid userId) => HostUserId == userId;

    public LobbySettings CurrentSettings => new(
        GameSlug, CapabilityVersion, Privacy, MaxPlayers, TimeControlId, Rated,
        ResolvedRegion, SpectatorPolicy, TieBreakRuleId, AiFillRequested);

    /// <summary>
    /// Every joined seat is ready. The host's seat is created ready and is re-marked ready on transfer, so this is
    /// simply "all joined members ready" — the host is never a blocker.
    /// </summary>
    public bool IsEveryoneReady => JoinedMembers.All(m => m.IsReady);

    /// <summary>
    /// Domain-side start preconditions. The full Start command additionally validates M3 blocks, M4 capabilities,
    /// M5 engine availability, and M8 readiness (6B/6C) — none of which this aggregate can see.
    /// </summary>
    public bool CanStart(DateTime nowUtc) =>
        State == LobbyState.Open
        && !IsExpired(nowUtc)
        && JoinedCount >= 2
        && IsEveryoneReady
        // Ranked start is disabled while AI fill is requested: M9 does not exist, so a "ranked" match with an
        // unfillable AI seat would either hang or silently become unranked.
        && !(Rated && AiFillRequested);

    // ── Mutations ────────────────────────────────────────────────────────────

    public LobbyOutcome Join(Guid userId, DateTime nowUtc)
    {
        var guard = GuardMutable(nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        if (FindJoinedMember(userId) is not null)
            return LobbyOutcome.AlreadyJoined;

        // Capacity is also backed by a transactional check in 6B: the last-seat loser of a concurrent join catches
        // 23505 on the member index and reruns the whole command, which re-reads and lands here on Full.
        if (JoinedCount >= MaxPlayers)
            return LobbyOutcome.Full;

        _members.Add(LobbyMember.Join(Id, userId, nowUtc, isReady: false));
        ResetNonHostReadiness();
        Mutated();
        return LobbyOutcome.Ok;
    }

    public LobbyLeaveResult Leave(Guid userId, DateTime nowUtc)
    {
        var guard = GuardMutable(nowUtc);
        if (guard != LobbyOutcome.Ok) return LobbyLeaveResult.Failed(guard);

        var member = FindJoinedMember(userId);
        if (member is null) return LobbyLeaveResult.Failed(LobbyOutcome.NotMember);

        member.Leave(nowUtc);

        if (!IsHost(userId))
        {
            ResetNonHostReadiness();
            Mutated();
            return new LobbyLeaveResult(LobbyOutcome.Ok, null, null);
        }

        // Host left: transfer to the longest-tenured eligible joined human, tie-broken by user id (Risk #3 — this
        // is fixed policy, so two clients can never disagree on who the host is). JoinedMembers is already in that
        // exact order.
        var successor = JoinedMembers.FirstOrDefault();
        if (successor is null)
        {
            CloseInternal(LobbyClosedReason.NoEligibleHost, nowUtc);
            Mutated();
            return new LobbyLeaveResult(LobbyOutcome.Ok, null, LobbyClosedReason.NoEligibleHost);
        }

        HostUserId = successor.UserId;
        successor.SetReadiness(true);   // the new host is implicitly ready
        ResetNonHostReadiness();
        Mutated();
        return new LobbyLeaveResult(LobbyOutcome.Ok, successor.UserId, null);
    }

    public LobbyOutcome Kick(Guid actorUserId, Guid targetUserId, DateTime nowUtc)
    {
        var guard = GuardHostAction(actorUserId, nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        if (actorUserId == targetUserId)
            return LobbyOutcome.InvalidTarget;   // the host cannot kick self; they leave instead

        var target = FindJoinedMember(targetUserId);
        if (target is null) return LobbyOutcome.InvalidTarget;

        target.Kick(actorUserId, nowUtc);
        ResetNonHostReadiness();
        Mutated();
        return LobbyOutcome.Ok;
    }

    public LobbyOutcome SetReadiness(Guid userId, bool isReady, DateTime nowUtc)
    {
        var guard = GuardMutable(nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        var member = FindJoinedMember(userId);
        if (member is null) return LobbyOutcome.NotMember;

        // The host is implicitly ready and cannot un-ready: their readiness is not a real signal, and letting them
        // clear it would create a lobby that can never satisfy IsEveryoneReady.
        if (IsHost(userId))
            return LobbyOutcome.InvalidTarget;

        member.SetReadiness(isReady);
        Mutated();
        return LobbyOutcome.Ok;
    }

    /// <summary>
    /// Host-only settings change. Resets all joined non-host readiness when the change is match-affecting
    /// (<see cref="LobbySettings.IsMatchAffectingChangeTo"/>) — a privacy or spectator-policy toggle alone does not
    /// invalidate a ready roster.
    /// </summary>
    public LobbyOutcome ChangeSettings(Guid actorUserId, LobbySettings settings, DateTime nowUtc)
    {
        var guard = GuardHostAction(actorUserId, nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        // Shrinking below the current roster would strand seated members with no defined eviction rule.
        if (settings.MaxPlayers < JoinedCount)
            return LobbyOutcome.Full;

        var matchAffecting = CurrentSettings.IsMatchAffectingChangeTo(settings);

        ApplySettings(settings);
        if (matchAffecting)
            ResetNonHostReadiness();

        Mutated();
        return LobbyOutcome.Ok;
    }

    /// <summary>
    /// Open -&gt; Starting. Called only inside the transaction that also commits exactly one MatchRequestedV1, and
    /// only while the M8 readiness probe is healthy. A committed request is a durable <em>request</em>, not a
    /// created match (Risk #6) — reaching Started requires M8's MatchCreatedV1.
    /// </summary>
    public LobbyOutcome BeginStarting(Guid actorUserId, DateTime nowUtc)
    {
        var guard = GuardHostAction(actorUserId, nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        if (!CanStart(nowUtc)) return LobbyOutcome.NotStartable;

        State = LobbyState.Starting;
        Mutated();
        return LobbyOutcome.Ok;
    }

    /// <summary>Starting -&gt; Started, on M8's MatchCreatedV1. Terminal.</summary>
    public LobbyOutcome MarkStarted()
    {
        if (State != LobbyState.Starting) return LobbyOutcome.NotStartable;

        State = LobbyState.Started;
        Mutated();
        return LobbyOutcome.Ok;
    }

    /// <summary>
    /// Starting -&gt; Open, on M8's recoverable MatchCreationFailedV1. Readiness is preserved unless the failure
    /// identified stale settings or membership, in which case every joined non-host human must re-confirm.
    /// </summary>
    public LobbyOutcome ReturnToOpen(bool resetReadiness, DateTime nowUtc)
    {
        if (State != LobbyState.Starting) return LobbyOutcome.NotStartable;

        // A lobby that expired while M8 was working does not silently reopen.
        if (IsExpired(nowUtc))
        {
            CloseInternal(LobbyClosedReason.Expired, nowUtc);
            State = LobbyState.Expired;
            Mutated();
            return LobbyOutcome.Expired;
        }

        State = LobbyState.Open;
        if (resetReadiness)
            ResetNonHostReadiness();

        Mutated();
        return LobbyOutcome.Ok;
    }

    public LobbyOutcome Close(LobbyClosedReason reason, DateTime nowUtc)
    {
        if (IsTerminal) return LobbyOutcome.Closed;

        CloseInternal(reason, nowUtc);
        Mutated();
        return LobbyOutcome.Ok;
    }

    /// <summary>
    /// Expiry sweep entry point (6C's expiry worker). Idempotent: returns false when the lobby is already terminal
    /// or not yet past its deadline, so a re-run of the sweep is a no-op rather than a second state change.
    /// </summary>
    public bool TryExpire(DateTime nowUtc)
    {
        if (IsTerminal || !IsExpired(nowUtc)) return false;

        State = LobbyState.Expired;
        ClosedReason = LobbyClosedReason.Expired;
        ReleaseAllJoinedMembers(nowUtc);
        Mutated();
        return true;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private LobbyOutcome GuardMutable(DateTime nowUtc)
    {
        if (IsTerminal) return LobbyOutcome.Closed;
        if (IsExpired(nowUtc)) return LobbyOutcome.Expired;
        return LobbyOutcome.Ok;
    }

    private LobbyOutcome GuardHostAction(Guid actorUserId, DateTime nowUtc)
    {
        var guard = GuardMutable(nowUtc);
        if (guard != LobbyOutcome.Ok) return guard;

        // A non-member gets the privacy-safe not-found rather than a 403 that would confirm the lobby exists.
        if (FindJoinedMember(actorUserId) is null) return LobbyOutcome.NotMember;
        if (!IsHost(actorUserId)) return LobbyOutcome.Forbidden;

        return LobbyOutcome.Ok;
    }

    /// <summary>
    /// Clears readiness for every joined member except the host, who is implicitly ready and is never counted in a
    /// reset (Risk #4).
    /// </summary>
    private void ResetNonHostReadiness()
    {
        foreach (var member in _members.Where(m => m.IsJoined && m.UserId != HostUserId))
            member.SetReadiness(false);
    }

    private void CloseInternal(LobbyClosedReason reason, DateTime nowUtc)
    {
        State = LobbyState.Closed;
        ClosedReason = reason;
        ReleaseAllJoinedMembers(nowUtc);
    }

    /// <summary>
    /// Releases every still-joined seat when the lobby itself goes terminal. Without this, a member who was never
    /// individually removed (e.g. the sole host of a lobby that expires) stays at <c>LobbyMemberState.Joined</c>
    /// forever — invisible to <c>GetActiveLobbyForUserAsync</c> (which filters on the lobby's own terminal state)
    /// but not to the DB's partial unique index on (UserId, Joined), permanently blocking that user from ever
    /// joining or creating another lobby.
    /// </summary>
    private void ReleaseAllJoinedMembers(DateTime nowUtc)
    {
        foreach (var member in _members.Where(m => m.IsJoined))
            member.Leave(nowUtc);
    }

    private void Mutated()
    {
        Revision += 1;
        Touch();
    }

    private void ApplySettings(LobbySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameSlug))
            throw new ArgumentException("GameSlug must not be empty.", nameof(settings));
        if (settings.CapabilityVersion < 1)
            throw new ArgumentException("CapabilityVersion must be at least 1.", nameof(settings));
        if (settings.MaxPlayers < 2)
            throw new ArgumentException("MaxPlayers must be at least 2 — a lobby is a multiplayer surface.", nameof(settings));
        if (!LobbyAllowLists.TimeControls.Contains(settings.TimeControlId))
            throw new ArgumentException($"TimeControlId '{settings.TimeControlId}' is not in the allow-list.", nameof(settings));
        if (!LobbyAllowLists.TieBreakRules.Contains(settings.TieBreakRuleId))
            throw new ArgumentException($"TieBreakRuleId '{settings.TieBreakRuleId}' is not in the allow-list.", nameof(settings));
        if (!LobbyRegion.IsResolved(settings.ResolvedRegion))
            throw new ArgumentException($"ResolvedRegion '{settings.ResolvedRegion}' must be an explicit allow-listed region, never 'Auto'.", nameof(settings));

        GameSlug = settings.GameSlug;
        CapabilityVersion = settings.CapabilityVersion;
        Privacy = settings.Privacy;
        MaxPlayers = settings.MaxPlayers;
        TimeControlId = settings.TimeControlId;
        Rated = settings.Rated;
        ResolvedRegion = settings.ResolvedRegion;
        SpectatorPolicy = settings.SpectatorPolicy;
        TieBreakRuleId = settings.TieBreakRuleId;
        AiFillRequested = settings.AiFillRequested;
    }
}
