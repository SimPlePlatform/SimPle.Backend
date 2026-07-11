using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The outcome of a <see cref="IGameHostInvoker"/> call that has no domain-level "rejection" shape of its own
/// (<see cref="GameStateEnvelope"/>, <see cref="PlayerViewEnvelope"/>, a terminal-result query). Unlike
/// <see cref="EngineTransition"/> — which encodes rejection natively because a command always has a prior
/// revision to report — these calls either produce a value or fail with a stable <see cref="EngineErrorCode"/>.
/// </summary>
public sealed class GameHostResult<T>
{
    public bool Succeeded { get; }
    public T? Value { get; }
    public EngineErrorCode? ErrorCode { get; }
    public string? ErrorDetail { get; }

    private GameHostResult(bool succeeded, T? value, EngineErrorCode? errorCode, string? errorDetail)
    {
        Succeeded = succeeded;
        Value = value;
        ErrorCode = errorCode;
        ErrorDetail = errorDetail;
    }

    public static GameHostResult<T> Success(T value) => new(true, value, null, null);

    public static GameHostResult<T> Failure(EngineErrorCode code, string? detail = null) => new(false, default, code, detail);
}
