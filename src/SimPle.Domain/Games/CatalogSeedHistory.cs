using SimPle.Domain.Common;

namespace SimPle.Domain.Games;

/// <summary>Records that a given catalog manifest version was applied, with its content checksum.</summary>
public class CatalogSeedHistory : Entity
{
    public string ManifestVersion { get; private set; } = default!;
    public string Checksum { get; private set; } = default!;
    public DateTime AppliedAtUtc { get; private set; }

    private CatalogSeedHistory() { }

    public static CatalogSeedHistory Record(string manifestVersion, string checksum) => new()
    {
        ManifestVersion = manifestVersion,
        Checksum = checksum,
        AppliedAtUtc = DateTime.UtcNow,
    };
}
