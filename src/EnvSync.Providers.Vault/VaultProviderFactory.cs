using System.Collections.Immutable;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Providers.Vault;

/// <summary>
/// Validates the <c>hashicorp-vault</c> settings and finds a token, without touching the network. The settings are
/// <c>address</c> (else <c>VAULT_ADDR</c>), <c>namespace</c> (else <c>VAULT_NAMESPACE</c>), <c>mount</c> (default
/// <c>secret</c>) and <c>kv</c> (1 or 2, default 2). The token comes from <c>VAULT_TOKEN</c>, else from <c>~/.vault-token</c>
/// (where <c>vault login</c> writes it); envsync never stores one. Unknown settings are rejected so a typo cannot pass silently.
/// </summary>
public sealed class VaultProviderFactory : ISecretProviderFactory
{
    public const string TypeName = "hashicorp-vault";

    private static readonly string[] KnownSettings = ["address", "namespace", "mount", "kv"];

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _homeDirectory;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public VaultProviderFactory(
        Func<string, string?>? getEnvironmentVariable = null,
        string? homeDirectory = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _handlerFactory = handlerFactory ?? CreateDefaultHandler;
    }

    public string Type => TypeName;

    public Result<ISecretProvider> Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = ImmutableArray.CreateBuilder<Error>();
        var alias = definition.Alias;

        foreach (var key in definition.Settings.Keys)
        {
            if (!KnownSettings.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(Misconfigured(alias, $"Unknown setting '{key}'. Supported settings: {string.Join(", ", KnownSettings)}."));
            }
        }

        var address = ReadAddress(definition, errors);
        var mount = ReadMount(definition, errors);
        var kvVersion = ReadKvVersion(definition, errors);
        var vaultNamespace = Setting(definition, "namespace") ?? _getEnvironmentVariable("VAULT_NAMESPACE");

        if (errors.Count > 0)
        {
            return Result<ISecretProvider>.Failure(errors.ToImmutable());
        }

        var token = FindToken();
        if (token is null)
        {
            return Result<ISecretProvider>.Failure(new Error(
                ErrorKind.AuthenticationFailed,
                alias,
                "No Vault token was found. Set VAULT_TOKEN, or run 'vault login' (the token is read from ~/.vault-token)."));
        }

        var options = new VaultOptions(address!, string.IsNullOrWhiteSpace(vaultNamespace) ? null : vaultNamespace.Trim(), mount!, kvVersion, new SecretValue(token));
        var http = new HttpClient(_handlerFactory()) { Timeout = Timeout.InfiniteTimeSpan };
        return Result<ISecretProvider>.Success(new VaultSecretProvider(http, options));
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    private Uri? ReadAddress(ProviderDefinition definition, ImmutableArray<Error>.Builder errors)
    {
        var text = Setting(definition, "address") ?? _getEnvironmentVariable("VAULT_ADDR");
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add(Misconfigured(definition.Alias, "The Vault server address is missing: set 'address' in the manifest or the VAULT_ADDR environment variable."));
            return null;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            errors.Add(Misconfigured(definition.Alias, $"'{text.Trim()}' is not a valid http(s) address, for example https://vault.example.com:8200."));
            return null;
        }

        if (address.Scheme == "http" && !address.IsLoopback)
        {
            errors.Add(Misconfigured(definition.Alias, "The Vault address must use https: with plain http the token would travel unencrypted. Plain http is only allowed for localhost."));
            return null;
        }

        return address;
    }

    private static string? ReadMount(ProviderDefinition definition, ImmutableArray<Error>.Builder errors)
    {
        var mount = (Setting(definition, "mount") ?? "secret").Trim().Trim('/');
        if (mount.Length > 0)
        {
            return mount;
        }

        errors.Add(Misconfigured(definition.Alias, "'mount' must name the path where the KV engine is mounted, for example \"secret\"."));
        return null;
    }

    private static int ReadKvVersion(ProviderDefinition definition, ImmutableArray<Error>.Builder errors)
    {
        // Absent means "use the default"; present but blank or invalid is a mistake worth reporting, not guessing.
        var text = definition.Settings.TryGetValue("kv", out var configured) ? configured.Trim() : "2";
        if (text is "1" or "2")
        {
            return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        errors.Add(Misconfigured(definition.Alias, $"'kv' must be 1 or 2, not '{text}'."));
        return 0;
    }

    private string? FindToken()
    {
        var fromEnvironment = _getEnvironmentVariable("VAULT_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        try
        {
            var fromFile = File.ReadAllText(Path.Combine(_homeDirectory, ".vault-token")).Trim();
            return fromFile.Length > 0 ? fromFile : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Setting(ProviderDefinition definition, string key) =>
        definition.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static Error Misconfigured(string alias, string detail) => new(ErrorKind.ProviderMisconfigured, alias, detail);
}
