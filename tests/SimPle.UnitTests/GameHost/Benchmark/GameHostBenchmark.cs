using System.Diagnostics;
using System.Runtime.InteropServices;
using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Reference;

namespace SimPle.UnitTests.GameHost.Benchmark;

/// <summary>
/// The D3 benchmark deviation: a minimal in-repo <see cref="Stopwatch"/> measurement of the fixed reference
/// workload (10,000 accepted commands after warm-up), instead of a BenchmarkDotNet dependency. Runs
/// <see cref="HiddenTokenDraftDefinition"/> matches back-to-back through <c>HostedGameDefinition.ApplyCommand</c>
/// — the same call path Module 8 will drive in production — timing each accepted command individually.
/// </summary>
public static class GameHostBenchmark
{
    public const int WarmupCommands = 500;
    public const int MeasuredCommands = 10_000;

    public sealed class BenchmarkResult
    {
        public double P50Ms { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
        public double MaxMs { get; init; }
        public long AllocatedBytes { get; init; }
        public int MaxSerializedStateBytes { get; init; }
        public int MaxSerializedCommandBytes { get; init; }
        public int MeasuredCommandCount { get; init; }
        public string Environment { get; init; } = "";

        /// <summary>Acceptance: p95 &lt; 25 ms.</summary>
        public bool MeetsP95Budget => P95Ms < 25.0;

        /// <summary>Acceptance: no command exceeds the 100 ms soft execution budget.</summary>
        public bool NoCommandOverSoftBudget => MaxMs <= EngineLimits.SoftExecutionBudget.TotalMilliseconds;
    }

    public static BenchmarkResult Run()
    {
        RunWorkload(WarmupCommands, samples: null, out _, out _);

        var samples = new List<double>(MeasuredCommands);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        RunWorkload(MeasuredCommands, samples, out var maxStateBytes, out var maxCommandBytes);
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        samples.Sort();

        return new BenchmarkResult
        {
            P50Ms = Percentile(samples, 0.50),
            P95Ms = Percentile(samples, 0.95),
            P99Ms = Percentile(samples, 0.99),
            MaxMs = samples[^1],
            AllocatedBytes = allocatedAfter - allocatedBefore,
            MaxSerializedStateBytes = maxStateBytes,
            MaxSerializedCommandBytes = maxCommandBytes,
            MeasuredCommandCount = samples.Count,
            Environment =
                $"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; " +
                $"{RuntimeInformation.ProcessArchitecture}; ProcessorCount={System.Environment.ProcessorCount}",
        };
    }

    /// <summary>
    /// Plays fixed-seat matches back-to-back, always drawing (the more expensive path — deserialize state,
    /// draw from the RNG, mutate hand, reserialize, checksum), until <paramref name="commandCount"/> commands
    /// have been accepted. Not seeded for cross-process reproducibility: unlike the golden vectors, the
    /// benchmark's numeric results are environment-dependent by nature — only the pass/fail thresholds matter.
    /// </summary>
    private static void RunWorkload(int commandCount, List<double>? samples, out int maxStateBytes, out int maxCommandBytes)
    {
        var hosted = new HostedGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView>(
            new HiddenTokenDraftDefinition());

        maxStateBytes = 0;
        maxCommandBytes = 0;
        var issued = 0;

        while (issued < commandCount)
        {
            var seats = new[]
            {
                new SeatAssignment(0, Guid.NewGuid(), false),
                new SeatAssignment(1, Guid.NewGuid(), false),
                new SeatAssignment(2, Guid.NewGuid(), false),
                new SeatAssignment(3, Guid.NewGuid(), false),
            };
            var setup = GameSetup.Create(seats, "multiplayer");
            var matchSeed = ((UInt128)(ulong)Random.Shared.NextInt64() << 64) | (ulong)Random.Shared.NextInt64();
            var state = hosted.CreateInitialState(setup, matchSeed, CancellationToken.None);

            var seat = 0;
            var expectedRevision = 0;

            while (issued < commandCount)
            {
                var command = new DrawTokenCommand();
                var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(command);
                maxCommandBytes = Math.Max(maxCommandBytes, payload.Length);

                var envelope = GameCommandEnvelope.Create(
                    Guid.NewGuid(), expectedRevision, seats[seat].UserId!.Value, seat, command.CommandType, payload);

                var stopwatch = Stopwatch.StartNew();
                var transition = hosted.ApplyCommand(state, envelope, CancellationToken.None);
                stopwatch.Stop();

                if (!transition.Accepted)
                    break; // Deck exhausted mid-round under this always-draw driver; start a fresh match.

                samples?.Add(stopwatch.Elapsed.TotalMilliseconds);
                issued++;
                maxStateBytes = Math.Max(maxStateBytes, transition.NextState!.StateBytes.Length);

                state = transition.NextState!;
                expectedRevision = transition.NextRevision;
                seat = (seat + 1) % seats.Length;

                if (transition.EngineState == EngineState.Terminal)
                    break;
            }
        }
    }

    private static double Percentile(List<double> sortedSamples, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sortedSamples.Count) - 1;
        index = Math.Clamp(index, 0, sortedSamples.Count - 1);
        return sortedSamples[index];
    }
}
