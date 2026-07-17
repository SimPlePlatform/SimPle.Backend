using System.Text;
using SimPle.Infrastructure.Games;

namespace SimPle.UnitTests.Games;

public sealed class GameCatalogSeedChecksumTests
{
    [Fact]
    public void ComputeManifestChecksum_IgnoresGitLineEndingNormalization()
    {
        const string lf = "{\n  \"manifestVersion\": \"2026.1\"\n}\n";
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        var lfChecksum = GameCatalogSeeder.ComputeManifestChecksum(Encoding.UTF8.GetBytes(lf));
        var crlfChecksum = GameCatalogSeeder.ComputeManifestChecksum(Encoding.UTF8.GetBytes(crlf));

        Assert.Equal(lfChecksum, crlfChecksum);
    }
}
