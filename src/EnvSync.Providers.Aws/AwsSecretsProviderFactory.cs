using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Diagnostics;
using EnvSync.Domain;

namespace EnvSync.Providers.Aws;

/// <summary>
/// Validates the <c>aws-secrets</c> settings and builds the provider. <c>region</c> is required (or <c>AWS_REGION</c> /
/// <c>AWS_DEFAULT_REGION</c>); <c>profile</c> is optional and selects a named profile from the shared AWS config. Without a
/// profile the SDK's own default chain finds credentials (environment, shared files, SSO, container or instance roles), so
/// envsync stores none. Building the client makes no request.
/// </summary>
public sealed class AwsSecretsProviderFactory : ISecretProviderFactory
{
    public const string TypeName = "aws-secrets";

    private static readonly string[] KnownSettings = ["region", "profile"];

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, AWSCredentials?> _resolveProfile;
    private readonly Func<AWSCredentials?, string, ISecretsManagerGateway> _createGateway;

    public AwsSecretsProviderFactory(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, AWSCredentials?>? resolveProfile = null,
        Func<AWSCredentials?, string, ISecretsManagerGateway>? createGateway = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _resolveProfile = resolveProfile ?? ResolveProfile;
        _createGateway = createGateway ?? CreateGateway;
    }

    public string Type => TypeName;

    public Result<ISecretProvider> Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var key in definition.Settings.Keys)
        {
            if (!KnownSettings.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Misconfigured(definition, $"Unknown setting '{key}'. Supported settings: {string.Join(", ", KnownSettings)}.");
            }
        }

        // Present but blank is a mistake, not "use the ambient value": a blank profile would quietly pick up whichever credentials
        // the machine happens to have, possibly for another AWS account.
        foreach (var key in KnownSettings)
        {
            if (definition.Settings.TryGetValue(key, out var raw) && string.IsNullOrWhiteSpace(raw))
            {
                return Misconfigured(definition, $"The '{key}' setting is present but empty: remove it or give it a value.");
            }
        }

        var region = Setting(definition, "region") ?? _getEnvironmentVariable("AWS_REGION") ?? _getEnvironmentVariable("AWS_DEFAULT_REGION");
        if (string.IsNullOrWhiteSpace(region))
        {
            return Misconfigured(definition, "The AWS region is missing: set 'region' in the manifest or the AWS_REGION environment variable.");
        }

        region = region.Trim();
        if (!IsValidRegion(region))
        {
            return Misconfigured(definition, $"'{region}' is not a valid AWS region name, for example us-east-1.");
        }

        AWSCredentials? credentials = null;
        if (Setting(definition, "profile") is { } profile)
        {
            try
            {
                credentials = _resolveProfile(profile);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Loading an SSO or assume-role profile pulls in extra SDK assemblies; a failure there must be a message, not a crash.
                return Misconfigured(definition, $"The AWS profile '{profile}' could not be loaded: {ErrorText.Summarize(exception.Message, 400)}");
            }

            if (credentials is null)
            {
                return Misconfigured(definition, $"The AWS profile '{profile}' was not found in the shared credentials or config files.");
            }
        }

        try
        {
            return Result<ISecretProvider>.Success(new AwsSecretsProvider(_createGateway(credentials, region)));
        }
        catch (AmazonClientException exception)
        {
            return Result<ISecretProvider>.Failure(new Error(
                ErrorKind.AuthenticationFailed,
                definition.Alias,
                $"The AWS client could not be created: {ErrorText.Summarize(exception.Message, 400)} Run 'aws configure' or 'aws sso login', or set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY."));
        }
    }

    /// <summary>Lowercase letters, digits and hyphens, shaped like <c>us-east-1</c>, <c>us-gov-west-1</c> or <c>cn-north-1</c>.</summary>
    private static bool IsValidRegion(string region)
    {
        var parts = region.Split('-');
        if (parts.Length < 3 || parts[0].Length < 2 || parts[^1].Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            {
                return false;
            }
        }

        return parts[0].All(char.IsAsciiLetterLower) && parts[^1].All(char.IsAsciiDigit);
    }

    private static AWSCredentials? ResolveProfile(string name) =>
        new CredentialProfileStoreChain().TryGetAWSCredentials(name, out var credentials) ? credentials : null;

    private static SdkSecretsManagerGateway CreateGateway(AWSCredentials? credentials, string region)
    {
        var endpoint = RegionEndpoint.GetBySystemName(region);
        return new SdkSecretsManagerGateway(
            credentials is null
                ? new AmazonSecretsManagerClient(endpoint)
                : new AmazonSecretsManagerClient(credentials, endpoint));
    }

    private static string? Setting(ProviderDefinition definition, string key) =>
        definition.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static Result<ISecretProvider> Misconfigured(ProviderDefinition definition, string detail) =>
        Result<ISecretProvider>.Failure(new Error(ErrorKind.ProviderMisconfigured, definition.Alias, detail));
}
