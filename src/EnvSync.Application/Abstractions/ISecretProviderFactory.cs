using EnvSync.Domain;

namespace EnvSync.Application.Abstractions;

/// <summary>
/// Builds the provider for one <see cref="ProviderDefinition"/>. Creating must not touch the network:
/// it only validates the settings, so a misconfiguration is reported before any request is made.
/// </summary>
public interface ISecretProviderFactory
{
    /// <summary>The <c>type</c> key used in the manifest, for example <c>azure-keyvault</c>.</summary>
    string Type { get; }

    Result<ISecretProvider> Create(ProviderDefinition definition);
}
