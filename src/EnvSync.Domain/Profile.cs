using System.Collections.Immutable;

namespace EnvSync.Domain;

/// <summary>A named environment (dev, staging...): the providers it can talk to and the variables it needs.</summary>
public sealed record Profile(
    string Name,
    ImmutableArray<ProviderDefinition> Providers,
    ImmutableArray<VariableSpec> Variables);
