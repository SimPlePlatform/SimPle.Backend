using SimPle.Domain.Common;

namespace SimPle.Domain.Capabilities;

/// <summary>
/// Records that a given capability manifest version was applied, with its content checksum.
///
/// Deliberately a separate table from Module 4's <c>catalog_seed_history</c> rather than a shared one: the two
/// manifests version independently, so a shared <c>ManifestVersion</c> key would collide the moment both seeders
/// happened to ship a "2026.1", and one seeder would read the other's checksum and refuse to run.
/// </summary>
public class CapabilitySeedHistory : Entity
{
    public string ManifestVersion { get; private set; } = default!;
    public string Checksum { get; private set; } = default!;
    public DateTime AppliedAtUtc { get; private set; }

    private CapabilitySeedHistory() { }

    public static CapabilitySeedHistory Record(string manifestVersion, string checksum, DateTime appliedAtUtc) => new()
    {
        ManifestVersion = manifestVersion,
        Checksum = checksum,
        AppliedAtUtc = appliedAtUtc,
    };
}
