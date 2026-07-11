using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>
/// One-shot generator, run manually to (re)materialize the committed golden-vector JSON files from
/// <see cref="HiddenTokenDraftGoldenVectorScenario"/>. Not a test — <see cref="GoldenVectorTests"/> is the
/// permanent, read-only comparison that runs every build. Updating a vector this way is itself the "engine/
/// schema version bump or documented bug-fix ADR" the spec requires before a vector may change.
/// </summary>
public static class GoldenVectorGenerator
{
    public static void Regenerate()
    {
        var directory = SourceDirectory();
        var vectors = HiddenTokenDraftGoldenVectorScenario.Build();

        Write(directory, "initial-envelope.json", vectors.InitialEnvelope);
        Write(directory, "accepted-command.json", vectors.AcceptedCommand);
        Write(directory, "rejected-command.json", vectors.RejectedCommand);
        Write(directory, "view-seat-0.json", vectors.ViewSeat0);
        Write(directory, "view-seat-1.json", vectors.ViewSeat1);
        Write(directory, "view-seat-2.json", vectors.ViewSeat2);
        Write(directory, "view-spectator.json", vectors.ViewSpectator);
        Write(directory, "terminal-result.json", vectors.TerminalResult);
        Write(directory, "corrupt-checksum.json", vectors.CorruptChecksum);
        Write(directory, "unsupported-version.json", vectors.UnsupportedVersion);
    }

    private static void Write<T>(string directory, string fileName, T value)
    {
        var json = JsonSerializer.Serialize(value, GoldenVectorJson.Options);
        File.WriteAllText(Path.Combine(directory, fileName), json + "\n");
    }

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") =>
        Path.GetDirectoryName(sourceFile)!;
}
