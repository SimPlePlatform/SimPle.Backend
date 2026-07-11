using System.Text.Json;

namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>Plain, human-diffable JSON options for the committed golden-vector files. Not the game-host codec.</summary>
internal static class GoldenVectorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
