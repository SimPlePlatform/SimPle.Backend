using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// These caps are a security control, not a tuning knob: they are what stops a payload bomb or an algorithmic
/// exhaustion attack at the host boundary. Pinning each value here means a change cannot be slipped in as an
/// incidental edit — it has to break a test, which forces the benchmark/security evidence and ADR the brief
/// requires.
/// </summary>
public sealed class EngineLimitsTests
{
    [Fact]
    public void SizeCaps_MatchThePublishedHardDefaults()
    {
        EngineLimits.MaxCommandPayloadBytes.Should().Be(16 * 1024);
        EngineLimits.MaxSerializedStateBytes.Should().Be(256 * 1024);
        EngineLimits.MaxPlayerViewBytes.Should().Be(256 * 1024);
        EngineLimits.MaxGameEventBatchBytes.Should().Be(64 * 1024);
    }

    [Fact]
    public void CountCaps_MatchThePublishedHardDefaults()
    {
        EngineLimits.MinPlayers.Should().Be(1);
        EngineLimits.MaxPlayers.Should().Be(8);
        EngineLimits.MaxEmittedEventsPerCommand.Should().Be(128);
    }

    [Fact]
    public void ExecutionBudget_MatchesThePublishedHardDefaults()
    {
        EngineLimits.SoftExecutionBudget.Should().Be(TimeSpan.FromMilliseconds(100));
        EngineLimits.CancellationRequestThreshold.Should().Be(TimeSpan.FromMilliseconds(500));
        EngineLimits.CooperativeReturnGrace.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void ExecutionBudget_IsOrderedSoftThenCancelThenGrace()
    {
        // The three thresholds only make sense as an escalation; an ordering mistake would make the host either
        // cancel work that is still within budget, or never cancel at all.
        EngineLimits.SoftExecutionBudget.Should().BeLessThan(EngineLimits.CancellationRequestThreshold);
        EngineLimits.CooperativeReturnGrace.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
