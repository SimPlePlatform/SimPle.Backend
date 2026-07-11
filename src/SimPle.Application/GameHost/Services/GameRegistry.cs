using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The single <see cref="IGameRegistry"/> implementation. Built once from the full set of installed definitions
/// via <see cref="Create"/> — normally called from the Api composition root before <c>app.Run()</c> — and is
/// immutable and safe to share as a singleton afterward.
/// </summary>
public sealed class GameRegistry : IGameRegistry
{
    private readonly IReadOnlyDictionary<(string Slug, int EngineVersion), IHostedGameDefinition> _definitions;
    private readonly IReadOnlyList<GameDefinitionMetadata> _registeredDefinitions;

    private GameRegistry(
        IReadOnlyDictionary<(string Slug, int EngineVersion), IHostedGameDefinition> definitions,
        IReadOnlyList<GameDefinitionMetadata> registeredDefinitions)
    {
        _definitions = definitions;
        _registeredDefinitions = registeredDefinitions;
    }

    /// <summary>
    /// Builds the registry from every installed definition. Throws <see cref="InvalidOperationException"/> on a
    /// duplicate <c>(Slug, EngineVersion)</c> key — a fail-fast startup error, per the spec's requirement that a
    /// duplicate key can never surface as a call-time ambiguity.
    /// </summary>
    public static GameRegistry Create(IEnumerable<IHostedGameDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var map = new Dictionary<(string Slug, int EngineVersion), IHostedGameDefinition>();
        var metadata = new List<GameDefinitionMetadata>();

        foreach (var definition in definitions)
        {
            var key = (definition.Metadata.Slug, definition.Metadata.EngineVersion);
            if (!map.TryAdd(key, definition))
            {
                throw new InvalidOperationException(
                    $"Duplicate game engine registration for '{definition.Metadata}'. " +
                    "Each (Slug, EngineVersion) pair must be registered exactly once.");
            }

            metadata.Add(definition.Metadata);
        }

        return new GameRegistry(map, metadata);
    }

    public bool TryResolve(string slug, int engineVersion, out IHostedGameDefinition? definition) =>
        _definitions.TryGetValue((slug, engineVersion), out definition);

    public IReadOnlyList<GameDefinitionMetadata> RegisteredDefinitions => _registeredDefinitions;
}
