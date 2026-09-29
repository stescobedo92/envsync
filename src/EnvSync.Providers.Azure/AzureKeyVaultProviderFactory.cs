using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Providers;
using EnvSync.Domain;

namespace EnvSync.Providers.Azure;

/// <summary>
/// Validates the <c>azure-keyvault</c> settings (only <c>uri</c>, which must be https) and builds a <see cref="SecretClient"/>
/// authenticated with <see cref="DefaultAzureCredential"/>: environment, workload identity, managed identity, Azure CLI,
/// Visual Studio and so on. envsync stores no Azure credential of its own. Building the client makes no request; the token is
/// only acquired on the first read.
/// <para>
/// The manifest is repository content, and the credential is offered to whatever host it names. Only genuine Key Vault and Managed
/// HSM hosts are therefore accepted, unless the user declared a host trusted through <c>ENVSYNC_TRUSTED_HOSTS</c>, which the
/// repository cannot influence.
/// </para>
/// </summary>
public sealed class AzureKeyVaultProviderFactory : ISecretProviderFactory
{
    public const string TypeName = "azure-keyvault";

    private static readonly string[] KeyVaultSuffixes =
    [
        ".vault.azure.net", ".vault.azure.cn", ".vault.usgovcloudapi.net", ".vault.microsoftazure.de",
        ".managedhsm.azure.net", ".managedhsm.azure.cn", ".managedhsm.usgovcloudapi.net",
    ];

    private readonly Func<Uri, SecretClient> _clientFactory;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public AzureKeyVaultProviderFactory(
        Func<Uri, SecretClient>? clientFactory = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _clientFactory = clientFactory ?? CreateClient;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

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

        if (!IsKeyVaultHost(uri.Host) && !TrustedHosts.FromEnvironment(_getEnvironmentVariable).IsTrusted(uri.Host))
        {
            return Misconfigured(
                definition,
                $"'{uri.Host}' is not an Azure Key Vault address (expected something like my-vault.vault.azure.net). Your Azure credential is only " +
                $"offered to Key Vault hosts; if this one is legitimate, list its host in {TrustedHosts.VariableName}.");
        }

        return Result<ISecretProvider>.Success(new AzureKeyVaultSecretProvider(_clientFactory(uri)));
    }

    private static bool IsKeyVaultHost(string host)
    {
        foreach (var suffix in KeyVaultSuffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static SecretClient CreateClient(Uri uri) => new(uri, new DefaultAzureCredential());

    private static Result<ISecretProvider> Misconfigured(ProviderDefinition definition, string detail) =>
        Result<ISecretProvider>.Failure(new Error(ErrorKind.ProviderMisconfigured, definition.Alias, detail));
}
