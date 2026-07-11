using System.Text.Json;
using FluentAssertions;

namespace SimPle.UnitTests.GameHost.Benchmark;

/// <summary>
/// Runs the D3 Stopwatch benchmark once and asserts its acceptance criteria: p95 &lt; 25 ms, and no single
/// command exceeds the 100 ms <c>EngineLimits.SoftExecutionBudget</c>. Results are also written to a local
/// evidence artifact (mirrors <see cref="GoldenVectors.GoldenVectorManifestTests.ManifestPath"/>'s pattern) —
/// the canonical cross-repo checkpoint recording happens separately, outside the test run.
/// </summary>
public class GameHostBenchmarkTests
{
    public static string ArtifactPath => Path.Combine(AppContext.BaseDirectory, "gamehost-benchmark.generated.json");

    [Fact]
    public void ReferenceWorkload_MeetsLatencyAndBudgetAcceptanceCriteria()
    {
        var result = GameHostBenchmark.Run();

        File.WriteAllText(
            ArtifactPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        result.MeasuredCommandCount.Should().Be(GameHostBenchmark.MeasuredCommands);
        result.MeetsP95Budget.Should().BeTrue($"p95 was {result.P95Ms:F3} ms, acceptance is < 25 ms");
        result.NoCommandOverSoftBudget.Should().BeTrue(
            $"max observed command latency was {result.MaxMs:F3} ms, acceptance is <= 100 ms (EngineLimits.SoftExecutionBudget)");
    }
}
