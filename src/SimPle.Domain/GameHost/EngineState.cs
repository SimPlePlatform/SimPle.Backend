namespace SimPle.Domain.GameHost;

/// <summary>
/// The only lifecycle a game definition is allowed to report. Module 8 owns the full match lifecycle
/// (Created/Active/Paused/Completed/Aborted) and decides whether a command may reach the engine at all;
/// the engine itself only knows whether its own rules consider the state finished.
/// </summary>
public enum EngineState
{
    InProgress = 0,
    Terminal = 1,
}
