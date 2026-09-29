using System.Collections.Immutable;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Providers;
using EnvSync.Domain;

namespace EnvSync.Providers.Vault;

/// <summary>
/// Validates the <c>hashicorp-vault</c> settings and finds a token, without touching the network. The settings are
/// <c>address</c> (else <c>VAULT_ADDR</c>), <c>namespace</c> (else <c>VAULT_NAMESPACE</c>), <c>mount</c> (default
/// <c>secret</c>) and <c>kv</c> (1 or 2, default 2). The token comes from <c>VAULT_TOKEN</c>, else from <c>~/.vault-token</c>
/// (where <c>vault login</c> writes it); envsync never stores one.
/// <para>
/// <strong>Where the token may go is not the manifest's decision.</strong> A manifest is repository content, and one that names
/// <c>"address": "https://attacker.example"</c> would receive the token the user holds for their own Vault. The manifest address is
/// therefore accepted only when it is loopback (a local dev server), the same origin as the user's own <c>VAULT_ADDR</c>, or a host
/// the user listed in <c>ENVSYNC_TRUSTED_HOSTS</c>. Unknown and blank settings are errors, so a typo cannot pass silently.
/// </para>
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
        var vaultNamespace = ReadNamespace(definition, errors);

        if (errors.Count > 0)
        {
            return Result<ISecretProvider>.Failure(errors.ToImmutable());
        }

        var token = FindToken(alias, out var tokenError);
        if (token is null)
        {
            return Result<ISecretProvider>.Failure(tokenError!.Value);
        }

        var options = new VaultOptions(address!, vaultNamespace, mount!, kvVersion, new SecretValue(token));
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
        var fromManifest = ReadSetting(definition, "address", errors, out var blank);
        if (blank)
        {
            return null;
        }

        var fromEnvironment = _getEnvironmentVariable("VAULT_ADDR");
        var text = fromManifest ?? (string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim());
        if (text is null)
        {
            errors.Add(Misconfigured(definition.Alias, "The Vault server address is missing: set 'address' in the manifest or the VAULT_ADDR environment variable."));
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            errors.Add(Misconfigured(definition.Alias, $"'{text}' is not a valid http(s) address, for example https://vault.example.com:8200."));
            return null;
        }

        if (address.Scheme == "http" && !address.IsLoopback)
        {
            errors.Add(Misconfigured(definition.Alias, "The Vault address must use https: with plain http the token would travel unencrypted. Plain http is only allowed for localhost."));
            return null;
        }

        if (fromManifest is not null && !IsTheUsersVault(address, fromEnvironment))
        {
            errors.Add(Misconfigured(
                definition.Alias,
                $"The manifest would send your Vault token to '{address.Host}', which is not your Vault: VAULT_ADDR is not set to that address. " +
                $"If it is your Vault, set VAULT_ADDR to it, or list its host in {TrustedHosts.VariableName}. A manifest must not decide where your token goes."));
            return null;
        }

        return address;
    }

    /// <summary>Loopback (a local dev server), the same origin as the user's own VAULT_ADDR, or a host the user declared trusted.</summary>
    private bool IsTheUsersVault(Uri address, string? vaultAddr)
    {
        if (address.IsLoopback)
        {
            return true;
        }

        if (Uri.TryCreate(vaultAddr?.Trim(), UriKind.Absolute, out var own)
            && Uri.Compare(address, own, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return true;
        }

        return TrustedHosts.FromEnvironment(_getEnvironmentVariable).IsTrusted(address.Host);
    }

    private static string? ReadMount(ProviderDefinition definition, ImmutableArray<Error>.Builder errors)
    {
        var configured = ReadSetting(definition, "mount", errors, out var blank);
        if (blank)
        {
            return null;
        }

        var mount = (configured ?? "secret").Trim('/');
        if (mount.Length > 0)
        {
            return mount;
        }

        errors.Add(Misconfigured(definition.Alias, "'mount' must name the path where the KV engine is mounted, for example \"secret\"."));
        return null;
    }

    private string? ReadNamespace(ProviderDefinition definition, ImmutableArray<Error>.Builder errors)
    {
        var configured = ReadSetting(definition, "namespace", errors, out _);
        if (configured is not null)
        {
            return configured;
        }

        var fromEnvironment = _getEnvironmentVariable("VAULT_NAMESPACE");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
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

    /// <summary>A setting that is absent is fine (the caller has a default); one that is present but blank is an error, never a silent fallback.</summary>
    private static string? ReadSetting(ProviderDefinition definition, string key, ImmutableArray<Error>.Builder errors, out bool blank)
    {
        blank = false;

        if (!definition.Settings.TryGetValue(key, out var raw))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(Misconfigured(definition.Alias, $"The '{key}' setting is present but empty: remove it or give it a value."));
            blank = true;
            return null;
        }

        return raw.Trim();
    }

    private string? FindToken(string alias, out Error? error)
    {
        error = null;

        var fromEnvironment = _getEnvironmentVariable("VAULT_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (_homeDirectory.Length > 0)
        {
            var path = Path.Combine(_homeDirectory, ".vault-token");
            try
            {
                var fromFile = File.ReadAllText(path).Trim();
                if (fromFile.Length > 0)
                {
                    return fromFile;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // No token file: fall through to the "no token" message below.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = new Error(ErrorKind.AuthenticationFailed, alias, $"The Vault token file '{path}' could not be read: {exception.Message}");
                return null;
            }
        }

        error = new Error(
            ErrorKind.AuthenticationFailed,
            alias,
            "No Vault token was found. Set VAULT_TOKEN, or run 'vault login' (the token is read from ~/.vault-token).");
        return null;
    }

    private static Error Misconfigured(string alias, string detail) => new(ErrorKind.ProviderMisconfigured, alias, detail);
}
