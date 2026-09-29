using System.Collections.Frozen;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Application.Providers;

/// <summary>
/// Maps a manifest <c>type</c> key to the factory that builds that provider. The list is passed in explicitly by the
/// composition root: nothing is discovered by scanning assemblies, so it stays trimming- and AOT-safe.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly FrozenDictionary<string, ISecretProviderFactory> _factories;
    private readonly string[] _types;

    public ProviderRegistry(IEnumerable<ISecretProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        var byType = new Dictionary<string, ISecretProviderFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories)
        {
            if (!byType.TryAdd(factory.Type, factory))
            {
                throw new ArgumentException($"More than one factory is registered for provider type '{factory.Type}'.", nameof(factories));
            }
        }

        _types = [.. byType.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        _factories = byType.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The registered provider types, sorted, for messages that tell the user what they can write.</summary>
    public IReadOnlyList<string> RegisteredTypes => _types;

    public bool IsRegistered(string type) => _factories.ContainsKey(type);

    public Result<ISecretProvider> Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return _factories.TryGetValue(definition.Type, out var factory)
            ? factory.Create(definition)
            : Result<ISecretProvider>.Failure(new Error(
                ErrorKind.ProviderUnknown,
                definition.Alias,
                $"Provider type '{definition.Type}' is not supported. Registered types: {string.Join(", ", _types)}."));
    }
}
