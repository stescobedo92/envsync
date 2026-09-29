using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Providers.Azure;

/// <summary>
/// Validates the <c>azure-keyvault</c> settings (only <c>uri</c>, which must be https) and builds a <see cref="SecretClient"/>
/// authenticated with <see cref="DefaultAzureCredential"/>: environment, workload identity, managed identity, Azure CLI,
/// Visual Studio and so on. envsync stores no Azure credential of its own. Building the client makes no request; the token is
/// only acquired on the first read.
/// </summary>
public sealed class AzureKeyVaultProviderFactory : ISecretProviderFactory
{
    public const string TypeName = "azure-keyvault";

    private readonly Func<Uri, SecretClient> _clientFactory;

    public AzureKeyVaultProviderFactory(Func<Uri, SecretClient>? clientFactory = null) =>
        _clientFactory = clientFactory ?? CreateClient;

    public string Type => TypeName;

    public Result<ISecretProvider> Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var key in definition.Settings.Keys)
        {
            if (!string.Equals(key, "uri", StringComparison.OrdinalIgnoreCase))
            {
                return Misconfigured(definition, $"Unknown setting '{key}'. The only supported setting is 'uri'.");
            }
        }

        if (!definition.Settings.TryGetValue("uri", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return Misconfigured(definition, "The vault address is missing: set 'uri', for example https://my-vault.vault.azure.net.");
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return Misconfigured(definition, $"'{text.Trim()}' is not a valid address, for example https://my-vault.vault.azure.net.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return Misconfigured(definition, "The Key Vault address must use https.");
        }

        return Result<ISecretProvider>.Success(new AzureKeyVaultSecretProvider(_clientFactory(uri)));
    }

    private static SecretClient CreateClient(Uri uri) => new(uri, new DefaultAzureCredential());

    private static Result<ISecretProvider> Misconfigured(ProviderDefinition definition, string detail) =>
        Result<ISecretProvider>.Failure(new Error(ErrorKind.ProviderMisconfigured, definition.Alias, detail));
}
