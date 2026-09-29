namespace EnvSync.Domain;

/// <summary>
/// A provider declared in the manifest. <paramref name="Settings"/> stays opaque on purpose: each provider's
/// factory validates and interprets its own keys, so a new provider never changes the manifest schema.
/// </summary>
public sealed record ProviderDefinition(string Alias, string Type, IReadOnlyDictionary<string, string> Settings);
