namespace SimPle.Domain.GameHost;

/// <summary>
/// The stable, client-safe rejection codes a hosted game call can return. The wire form is the
/// <c>Engine.*</c> string produced by <see cref="EngineErrorCodeExtensions.ToStableCode"/>; the enum member
/// names are an implementation detail, the strings are the contract and must not change.
/// </summary>
public enum EngineErrorCode
{
    UnknownGame,
    UnknownVersion,
    UnsupportedStateVersion,
    CorruptState,
    InvalidCommandType,
    InvalidCommand,
    IllegalActor,
    StaleRevision,
    PayloadTooLarge,
    StateTooLarge,
    Cancelled,
    ExecutionBudgetExceeded,
    PluginFailure,
}

public static class EngineErrorCodeExtensions
{
    /// <summary>
    /// Maps a code to its stable wire string. These 13 strings are a published contract shared with Module 8
    /// and any future client; renaming one is a breaking change.
    /// </summary>
    public static string ToStableCode(this EngineErrorCode code) => code switch
    {
        EngineErrorCode.UnknownGame => "Engine.UnknownGame",
        EngineErrorCode.UnknownVersion => "Engine.UnknownVersion",
        EngineErrorCode.UnsupportedStateVersion => "Engine.UnsupportedStateVersion",
        EngineErrorCode.CorruptState => "Engine.CorruptState",
        EngineErrorCode.InvalidCommandType => "Engine.InvalidCommandType",
        EngineErrorCode.InvalidCommand => "Engine.InvalidCommand",
        EngineErrorCode.IllegalActor => "Engine.IllegalActor",
        EngineErrorCode.StaleRevision => "Engine.StaleRevision",
        EngineErrorCode.PayloadTooLarge => "Engine.PayloadTooLarge",
        EngineErrorCode.StateTooLarge => "Engine.StateTooLarge",
        EngineErrorCode.Cancelled => "Engine.Cancelled",
        EngineErrorCode.ExecutionBudgetExceeded => "Engine.ExecutionBudgetExceeded",
        EngineErrorCode.PluginFailure => "Engine.PluginFailure",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped engine error code."),
    };
}
