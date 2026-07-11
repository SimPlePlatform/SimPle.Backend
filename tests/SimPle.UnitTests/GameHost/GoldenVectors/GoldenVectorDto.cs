namespace SimPle.UnitTests.GameHost.GoldenVectors;

/// <summary>
/// Plain data shapes for the committed golden-vector JSON files. Deliberately a separate, plain
/// <c>System.Text.Json</c> shape from <c>GameHostJsonContext</c> — that codec serializes a definition's own
/// state/command/view payload bytes under the pinned game-host options; these types describe the surrounding
/// envelope metadata for a human/CI reviewer and any future non-C# port, so they carry no dependency on the
/// production codec's fail-closed configuration.
/// </summary>
public sealed class GoldenEnvelopeVector
{
    public string GameSlug { get; set; } = "";
    public int EngineVersion { get; set; }
    public int StateSchemaVersion { get; set; }
    public int Revision { get; set; }
    public string RngAlgorithm { get; set; } = "";
    public ulong RngState { get; set; }
    public ulong RngInc { get; set; }
    public ulong RngCursor { get; set; }
    public string StateBytesBase64 { get; set; } = "";
    public string ChecksumSha256Hex { get; set; } = "";
}

public sealed class GoldenTransitionVector
{
    public bool Accepted { get; set; }
    public int PriorRevision { get; set; }
    public int NextRevision { get; set; }
    public string? RejectionCode { get; set; }
    public string? RejectionDetail { get; set; }
    public GoldenEnvelopeVector? NextState { get; set; }
    public List<string> PublicEventTypes { get; set; } = new();
    public List<string> PrivateEventTypes { get; set; } = new();
    public string EngineState { get; set; } = "";
}

public sealed class GoldenViewVector
{
    public string ViewerRole { get; set; } = "";
    public int? ViewerSeat { get; set; }
    public int Revision { get; set; }
    public string PublicViewBase64 { get; set; } = "";
    public bool HasPrivateView { get; set; }
    public string? PrivateViewBase64 { get; set; }
    public string EngineState { get; set; } = "";
}

public sealed class GoldenSeatResultVector
{
    public int Seat { get; set; }
    public string Outcome { get; set; } = "";
    public int Score { get; set; }
}

public sealed class GoldenTerminalResultVector
{
    public List<GoldenSeatResultVector> SeatResults { get; set; } = new();
}

/// <summary>A fail-closed scenario: a deliberately invalid envelope paired with the error code it must produce.</summary>
public sealed class GoldenFailureVector
{
    public string Scenario { get; set; } = "";
    public GoldenEnvelopeVector Envelope { get; set; } = new();
    public string ExpectedErrorCode { get; set; } = "";
}
