using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The 13 <c>Engine.*</c> strings are a published contract shared with Module 8 and, through it, with clients.
/// The enum member names are free to be refactored; the strings are not. This test is the thing that makes
/// that distinction real.
/// </summary>
public sealed class EngineErrorCodeTests
{
    [Theory]
    [InlineData(EngineErrorCode.UnknownGame, "Engine.UnknownGame")]
    [InlineData(EngineErrorCode.UnknownVersion, "Engine.UnknownVersion")]
    [InlineData(EngineErrorCode.UnsupportedStateVersion, "Engine.UnsupportedStateVersion")]
    [InlineData(EngineErrorCode.CorruptState, "Engine.CorruptState")]
    [InlineData(EngineErrorCode.InvalidCommandType, "Engine.InvalidCommandType")]
    [InlineData(EngineErrorCode.InvalidCommand, "Engine.InvalidCommand")]
    [InlineData(EngineErrorCode.IllegalActor, "Engine.IllegalActor")]
    [InlineData(EngineErrorCode.StaleRevision, "Engine.StaleRevision")]
    [InlineData(EngineErrorCode.PayloadTooLarge, "Engine.PayloadTooLarge")]
    [InlineData(EngineErrorCode.StateTooLarge, "Engine.StateTooLarge")]
    [InlineData(EngineErrorCode.Cancelled, "Engine.Cancelled")]
    [InlineData(EngineErrorCode.ExecutionBudgetExceeded, "Engine.ExecutionBudgetExceeded")]
    [InlineData(EngineErrorCode.PluginFailure, "Engine.PluginFailure")]
    public void ToStableCode_ReturnsThePublishedWireString(EngineErrorCode code, string expected)
    {
        code.ToStableCode().Should().Be(expected);
    }

    [Fact]
    public void EveryDeclaredCode_HasAStableMapping()
    {
        // Guards the gap the Theory above cannot see: a 14th code added to the enum without a mapping would
        // otherwise only blow up at runtime, inside a rejection path, in production.
        var declared = Enum.GetValues<EngineErrorCode>();

        declared.Should().HaveCount(13);
        foreach (var code in declared)
            code.ToStableCode().Should().StartWith("Engine.");
    }

    [Fact]
    public void ToStableCode_ForAnUndeclaredValue_Throws()
    {
        var bogus = (EngineErrorCode)999;

        var map = () => bogus.ToStableCode();

        map.Should().Throw<ArgumentOutOfRangeException>();
    }
}
