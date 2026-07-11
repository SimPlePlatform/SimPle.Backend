using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>
/// Produces the golden-vector SHA-256 manifest the module-05 spec's two-process determinism proof compares.
/// The manifest itself is not the proof — running the filtered <c>GameHost</c> suite in two separate
/// <c>dotnet test</c> processes and diffing <see cref="ManifestPath"/> between the two runs is (recorded at the
/// verification checkpoint, per deviation D3). This test only guarantees the manifest is written every run and
/// is itself a deterministic function of <see cref="HiddenTokenDraftGoldenVectorScenario"/>.
/// </summary>
public class GoldenVectorManifestTests
{
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "golden-vector-manifest.generated.json");

    [Fact]
    public void Manifest_IsWrittenAndSha256HashesAreStableAcrossRebuilds()
    {
        var vectors = HiddenTokenDraftGoldenVectorScenario.Build();

        var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["initial-envelope"] = Hash(vectors.InitialEnvelope),
            ["accepted-command"] = Hash(vectors.AcceptedCommand),
            ["rejected-command"] = Hash(vectors.RejectedCommand),
            ["view-seat-0"] = Hash(vectors.ViewSeat0),
            ["view-seat-1"] = Hash(vectors.ViewSeat1),
            ["view-seat-2"] = Hash(vectors.ViewSeat2),
            ["view-spectator"] = Hash(vectors.ViewSpectator),
            ["terminal-result"] = Hash(vectors.TerminalResult),
            ["corrupt-checksum"] = Hash(vectors.CorruptChecksum),
            ["unsupported-version"] = Hash(vectors.UnsupportedVersion),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, GoldenVectorJson.Options);
        File.WriteAllText(ManifestPath, manifestJson);

        // Recomputing in the same process must reproduce byte-identical hashes — the in-process half of the
        // determinism guarantee. The cross-process half is proven by diffing this file between two separate
        // `dotnet test --filter "FullyQualifiedName~GameHost"` runs at the verification checkpoint.
        var recomputed = HiddenTokenDraftGoldenVectorScenario.Build();
        Hash(recomputed.InitialEnvelope).Should().Be(manifest["initial-envelope"]);
        Hash(recomputed.TerminalResult).Should().Be(manifest["terminal-result"]);
    }

    private static string Hash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, GoldenVectorJson.Options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
