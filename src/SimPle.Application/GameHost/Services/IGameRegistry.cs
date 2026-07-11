using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The set of game engines installed at startup, keyed by the immutable <c>(Slug, EngineVersion)</c> pair. Built
/// once during composition-root startup and never mutated afterward — there is no runtime registration API,
/// so a duplicate key is a startup failure, never a call-time race.
/// </summary>
public interface IGameRegistry
{
    /// <summary>
    /// Resolves the exact engine for <paramref name="slug"/> at <paramref name="engineVersion"/>. Never falls
    /// back to "latest" or to a different version: a match record always names the exact engine that produced
    /// it, and replaying that match must resolve the same one.
    /// </summary>
    bool TryResolve(string slug, int engineVersion, out IHostedGameDefinition? definition);

    /// <summary>Every installed definition's metadata, for startup catalog-compatibility validation and diagnostics.</summary>
    IReadOnlyList<GameDefinitionMetadata> RegisteredDefinitions { get; }
}
