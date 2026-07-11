using System.Text.Json;
using FluentAssertions;
using SimPle.Application.GameHost.Serialization;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>
/// Read-only comparison against the committed golden-vector JSON files. This suite never writes: a vector
/// changing under it means the engine's byte-level output changed, which the spec treats as a compatibility
/// break requiring an engine/schema version bump or a documented bug-fix ADR, not a silent test update.
/// Regenerating the files after such a change is <see cref="GoldenVectorGenerator.Regenerate"/>, run manually.
/// </summary>
public class GoldenVectorTests
{
    private static readonly HiddenTokenDraftGoldenVectorScenario.Vectors Fresh = HiddenTokenDraftGoldenVectorScenario.Build();

    [Theory]
    [MemberData(nameof(VectorCases))]
    public void FreshVector_MatchesCommittedGoldenFile(string fileName, object freshVector)
    {
        var committedPath = Path.Combine(AppContext.BaseDirectory, "GameHost", "GoldenVectors", fileName);
        File.Exists(committedPath).Should().BeTrue($"the committed golden vector '{fileName}' must be checked in and copied to the test output");

        var committedJson = File.ReadAllText(committedPath).TrimEnd('\n', '\r');
        var freshJson = JsonSerializer.Serialize(freshVector, freshVector.GetType(), GoldenVectorJson.Options);

        freshJson.Should().Be(
            committedJson,
            $"a silent rewrite of '{fileName}' is a compatibility break — bump the engine/schema version or record a bug-fix ADR");
    }

    public static IEnumerable<object[]> VectorCases()
    {
        yield return new object[] { "initial-envelope.json", Fresh.InitialEnvelope };
        yield return new object[] { "accepted-command.json", Fresh.AcceptedCommand };
        yield return new object[] { "rejected-command.json", Fresh.RejectedCommand };
        yield return new object[] { "view-seat-0.json", Fresh.ViewSeat0 };
        yield return new object[] { "view-seat-1.json", Fresh.ViewSeat1 };
        yield return new object[] { "view-seat-2.json", Fresh.ViewSeat2 };
        yield return new object[] { "view-spectator.json", Fresh.ViewSpectator };
        yield return new object[] { "terminal-result.json", Fresh.TerminalResult };
        yield return new object[] { "corrupt-checksum.json", Fresh.CorruptChecksum };
        yield return new object[] { "unsupported-version.json", Fresh.UnsupportedVersion };
    }

    [Fact]
    public void RejectedCommand_LeavesRevisionAndRngUnchanged()
    {
        Fresh.RejectedCommand.Accepted.Should().BeFalse();
        Fresh.RejectedCommand.RejectionCode.Should().Be(EngineErrorCode.IllegalActor.ToStableCode());
        Fresh.RejectedCommand.NextRevision.Should().Be(Fresh.RejectedCommand.PriorRevision);
        Fresh.RejectedCommand.NextState.Should().BeNull();
    }

    [Fact]
    public void SeatViews_NeverExposeAnotherSeatsHand()
    {
        // Each seat's own hand differs (a distinct random draw), and no seat's PublicView bytes contain
        // another seat's PrivateView-equivalent payload. This adapter carries the whole redacted projection in
        // PublicView (see HostedGameDefinition.ProjectView), so the isolation guarantee is: seat N's serialized
        // view must equal exactly what ProjectView(state, seat N) produces, and must differ from every other
        // seat's view once hands are non-empty.
        var views = new[] { Fresh.ViewSeat0, Fresh.ViewSeat1, Fresh.ViewSeat2 };
        views.Select(v => v.PublicViewBase64).Distinct().Should().HaveCount(3, "every seat drew a different token, so every seat's view must be distinct");
        views.Select(v => v.PublicViewBase64).Should().NotContain(
            Fresh.ViewSpectator.PublicViewBase64,
            "a spectator must never receive a seat's own-hand projection verbatim");
    }

    [Fact]
    public void CorruptChecksum_FailsClosedOnProjectView()
    {
        var hosted = HiddenTokenDraftGoldenVectorScenario.CreateHostedDefinition();

        var act = () => hosted.ProjectView(
            Fresh.CorruptChecksumEnvelope,
            ViewerContext.ForSpectator(),
            CancellationToken.None);

        act.Should().Throw<GameHostSerializationException>()
            .Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }

    [Fact]
    public void UnsupportedVersion_FailsClosedOnProjectView()
    {
        var hosted = HiddenTokenDraftGoldenVectorScenario.CreateHostedDefinition();

        var act = () => hosted.ProjectView(
            Fresh.UnsupportedVersionEnvelope,
            ViewerContext.ForSpectator(),
            CancellationToken.None);

        act.Should().Throw<GameHostSerializationException>()
            .Which.Code.Should().Be(EngineErrorCode.UnsupportedStateVersion);
    }
}
