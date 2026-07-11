using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Reference;
using SimPle.UnitTests.GameHost.Support;

namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>
/// The complete, fixed set of golden vectors for the module-05 spec's required coverage: initial envelope,
/// accepted command, rejected command, every seat + spectator view, terminal result, corrupt checksum, and
/// unsupported version. Every input (match seed, seats, command ids, command order) is a hardcoded constant, so
/// re-running <see cref="Build"/> is byte-identical run to run and process to process — that determinism is
/// exactly what the committed JSON files and the two-process SHA-256 comparison are checking.
/// </summary>
public static class HiddenTokenDraftGoldenVectorScenario
{
    public static readonly Guid Seat0User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Seat1User = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Seat2User = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly UInt128 FixedMatchSeed = ((UInt128)0x0123456789ABCDEFUL << 64) | 0xFEDCBA9876543210UL;

    public sealed class Vectors
    {
        public required GoldenEnvelopeVector InitialEnvelope { get; init; }
        public required GoldenTransitionVector AcceptedCommand { get; init; }
        public required GoldenTransitionVector RejectedCommand { get; init; }
        public required GoldenViewVector ViewSeat0 { get; init; }
        public required GoldenViewVector ViewSeat1 { get; init; }
        public required GoldenViewVector ViewSeat2 { get; init; }
        public required GoldenViewVector ViewSpectator { get; init; }
        public required GoldenTerminalResultVector TerminalResult { get; init; }
        public required GoldenFailureVector CorruptChecksum { get; init; }
        public required GoldenFailureVector UnsupportedVersion { get; init; }

        /// <summary>The raw envelope behind <see cref="CorruptChecksum"/>, for fail-closed behavior assertions.</summary>
        public required GameStateEnvelope CorruptChecksumEnvelope { get; init; }

        /// <summary>The raw envelope behind <see cref="UnsupportedVersion"/>, for fail-closed behavior assertions.</summary>
        public required GameStateEnvelope UnsupportedVersionEnvelope { get; init; }
    }

    public static IHostedGameDefinition CreateHostedDefinition() =>
        new HostedGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView>(
            new HiddenTokenDraftDefinition());

    public static Vectors Build()
    {
        var hosted = CreateHostedDefinition();

        var threeSeatSetup = GameSetup.Create(
            new[]
            {
                new SeatAssignment(0, Seat0User, false),
                new SeatAssignment(1, Seat1User, false),
                new SeatAssignment(2, Seat2User, false),
            },
            "multiplayer");

        var initial = hosted.CreateInitialState(threeSeatSetup, FixedMatchSeed, CancellationToken.None);

        // Step 1: seat 0 draws on the opening turn — accepted.
        var afterSeat0 = ApplyDraw(hosted, initial, actorSeat: 0, expectedRevision: 0, commandId: CommandId(1));

        // Step 2: seat 2 tries to act while it is seat 1's turn — rejected (IllegalActor), revision unchanged.
        var outOfTurn = ApplyDraw(hosted, afterSeat0.NextState!, actorSeat: 2, expectedRevision: 1, commandId: CommandId(2));

        // Step 3: seat 1 draws correctly — accepted.
        var afterSeat1 = ApplyDraw(hosted, afterSeat0.NextState!, actorSeat: 1, expectedRevision: 1, commandId: CommandId(3));

        // Step 4: seat 2 draws correctly — accepted. Every seat now holds exactly one token, so each view below
        // has non-empty (and non-identical) hidden hands to prove hidden-view isolation.
        var afterSeat2 = ApplyDraw(hosted, afterSeat1.NextState!, actorSeat: 2, expectedRevision: 2, commandId: CommandId(4));
        var midGameState = afterSeat2.NextState!;

        var viewSeat0 = ToViewVector(hosted.ProjectView(midGameState, ViewerContext.ForPlayer(0, Seat0User), CancellationToken.None));
        var viewSeat1 = ToViewVector(hosted.ProjectView(midGameState, ViewerContext.ForPlayer(1, Seat1User), CancellationToken.None));
        var viewSeat2 = ToViewVector(hosted.ProjectView(midGameState, ViewerContext.ForPlayer(2, Seat2User), CancellationToken.None));
        var viewSpectator = ToViewVector(hosted.ProjectView(midGameState, ViewerContext.ForSpectator(), CancellationToken.None));

        // Terminal scenario: a separate 2-seat match where both seats pass once each — a full round of
        // consecutive passes ends the match immediately, without needing to drain the token pool.
        var twoSeatSetup = GameSetup.Create(
            new[]
            {
                new SeatAssignment(0, Seat0User, false),
                new SeatAssignment(1, Seat1User, false),
            },
            "multiplayer");
        var terminalInitial = hosted.CreateInitialState(twoSeatSetup, FixedMatchSeed, CancellationToken.None);
        var pass0 = ApplyPass(hosted, terminalInitial, actorSeat: 0, expectedRevision: 0, commandId: CommandId(5));
        var pass1 = ApplyPass(hosted, pass0.NextState!, actorSeat: 1, expectedRevision: 1, commandId: CommandId(6));

        var corrupted = GameStateEnvelopeTestFactory.WithTamperedChecksum(midGameState);
        var unsupportedVersion = GameStateEnvelope.Create(
            midGameState.GameSlug,
            midGameState.EngineVersion,
            stateSchemaVersion: 999,
            midGameState.Revision,
            midGameState.RngState,
            midGameState.StateBytes.Span);

        return new Vectors
        {
            InitialEnvelope = ToEnvelopeVector(initial),
            AcceptedCommand = ToTransitionVector(afterSeat0),
            RejectedCommand = ToTransitionVector(outOfTurn),
            ViewSeat0 = viewSeat0,
            ViewSeat1 = viewSeat1,
            ViewSeat2 = viewSeat2,
            ViewSpectator = viewSpectator,
            TerminalResult = ToTerminalResultVector(pass1.TerminalResult!),
            CorruptChecksum = new GoldenFailureVector
            {
                Scenario = "corrupt-checksum",
                Envelope = ToEnvelopeVector(corrupted),
                ExpectedErrorCode = EngineErrorCode.CorruptState.ToStableCode(),
            },
            UnsupportedVersion = new GoldenFailureVector
            {
                Scenario = "unsupported-version",
                Envelope = ToEnvelopeVector(unsupportedVersion),
                ExpectedErrorCode = EngineErrorCode.UnsupportedStateVersion.ToStableCode(),
            },
            CorruptChecksumEnvelope = corrupted,
            UnsupportedVersionEnvelope = unsupportedVersion,
        };
    }

    private static EngineTransition ApplyDraw(
        IHostedGameDefinition hosted, GameStateEnvelope state, int actorSeat, int expectedRevision, Guid commandId) =>
        ApplyCommand(hosted, state, new DrawTokenCommand(), actorSeat, expectedRevision, commandId);

    private static EngineTransition ApplyPass(
        IHostedGameDefinition hosted, GameStateEnvelope state, int actorSeat, int expectedRevision, Guid commandId) =>
        ApplyCommand(hosted, state, new PassTurnCommand(), actorSeat, expectedRevision, commandId);

    private static EngineTransition ApplyCommand(
        IHostedGameDefinition hosted,
        GameStateEnvelope state,
        HiddenTokenDraftCommand command,
        int actorSeat,
        int expectedRevision,
        Guid commandId)
    {
        var actorUserId = actorSeat switch
        {
            0 => Seat0User,
            1 => Seat1User,
            2 => Seat2User,
            _ => throw new ArgumentOutOfRangeException(nameof(actorSeat)),
        };

        var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(command);
        var envelope = GameCommandEnvelope.Create(commandId, expectedRevision, actorUserId, actorSeat, command.CommandType, payload);
        return hosted.ApplyCommand(state, envelope, CancellationToken.None);
    }

    private static Guid CommandId(int index) => new($"00000000-0000-0000-0000-{index:D12}");

    private static GoldenEnvelopeVector ToEnvelopeVector(GameStateEnvelope envelope) => new()
    {
        GameSlug = envelope.GameSlug,
        EngineVersion = envelope.EngineVersion,
        StateSchemaVersion = envelope.StateSchemaVersion,
        Revision = envelope.Revision,
        RngAlgorithm = envelope.RngAlgorithm,
        RngState = envelope.RngState.State,
        RngInc = envelope.RngState.Inc,
        RngCursor = envelope.RngState.Cursor,
        StateBytesBase64 = Convert.ToBase64String(envelope.StateBytes.Span),
        ChecksumSha256Hex = envelope.Checksum,
    };

    private static GoldenTransitionVector ToTransitionVector(EngineTransition transition) => new()
    {
        Accepted = transition.Accepted,
        PriorRevision = transition.PriorRevision,
        NextRevision = transition.NextRevision,
        RejectionCode = transition.RejectionCode,
        RejectionDetail = transition.RejectionDetail,
        NextState = transition.NextState is null ? null : ToEnvelopeVector(transition.NextState),
        PublicEventTypes = transition.PublicEvents.Select(e => e.EventType).ToList(),
        PrivateEventTypes = transition.PrivateEvents.Select(e => e.EventType).ToList(),
        EngineState = transition.EngineState.ToString(),
    };

    private static GoldenViewVector ToViewVector(PlayerViewEnvelope view) => new()
    {
        ViewerRole = view.ViewerRole.ToString(),
        ViewerSeat = view.ViewerSeat,
        Revision = view.Revision,
        PublicViewBase64 = Convert.ToBase64String(view.PublicView.Span),
        HasPrivateView = view.PrivateView.HasValue,
        PrivateViewBase64 = view.PrivateView.HasValue ? Convert.ToBase64String(view.PrivateView.Value.Span) : null,
        EngineState = view.EngineState.ToString(),
    };

    private static GoldenTerminalResultVector ToTerminalResultVector(TerminalResultCandidate terminal) => new()
    {
        SeatResults = terminal.SeatResults
            .Select(r => new GoldenSeatResultVector { Seat = r.Seat, Outcome = r.Outcome.ToString(), Score = r.Score })
            .ToList(),
    };
}
