using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Diagnostics;
using EnvSync.Application.Secrets;
using EnvSync.Domain;

namespace EnvSync.Providers.Azure;

/// <summary>
/// Reads the latest version of a Key Vault secret. SDK exceptions are translated here into typed errors, using only the
/// HTTP status and error code: the SDK's own messages carry the whole response, which is not ours to echo.
/// </summary>
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private const int MaxNameLength = 127;
    private const int MaxDetailLength = 600;

    private readonly SecretClient _client;

    public AzureKeyVaultSecretProvider(SecretClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async ValueTask<Result<SecretValue>> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (!IsValidName(reference.Path))
        {
            return Fail(
                reference,
                ErrorKind.ManifestInvalid,
                $"'{reference.Path}' is not a valid Key Vault secret name: only letters, digits and '-' are allowed (1-127 characters). " +
                "The 'ref' is the secret's name in the vault, not the environment variable's name.");
        }

        try
        {
            var response = await _client.GetSecretAsync(reference.Path, version: null, cancellationToken: cancellationToken);
            return StructuredSecret.Select(response.Value.Value ?? string.Empty, reference);
        }
        catch (RequestFailedException exception)
        {
            return Translate(exception, reference);
        }
        catch (AuthenticationFailedException exception) when (exception.InnerException is OperationCanceledException)
        {
            // A token request that was cancelled or timed out is not a credential problem. Surfacing it as a cancellation lets the
            // caller tell a timeout from the user pressing Ctrl+C, instead of blaming the user's login.
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (AuthenticationFailedException exception)
        {
            return Fail(
                reference,
                ErrorKind.AuthenticationFailed,
                $"Azure authentication failed: {ErrorText.Summarize(exception.Message, MaxDetailLength)} Run 'az login', or configure the AZURE_CLIENT_ID, AZURE_TENANT_ID and AZURE_CLIENT_SECRET environment variables.");
        }
    }

    private static Result<SecretValue> Translate(RequestFailedException exception, SecretReference reference)
    {
        var code = string.IsNullOrEmpty(exception.ErrorCode) ? string.Empty : $" ({exception.ErrorCode})";

        return exception.Status switch
        {
            404 => Fail(reference, ErrorKind.SecretNotFound, $"Secret '{reference.Path}' was not found in the vault."),
            403 when exception.ErrorCode == "SecretDisabled" =>
                Fail(reference, ErrorKind.SecretNotFound, $"Secret '{reference.Path}' exists but is disabled."),
            401 or 403 => Fail(
                reference,
                ErrorKind.AuthenticationFailed,
                $"Key Vault denied access (HTTP {exception.Status}{code}): give the identity the 'Key Vault Secrets User' role (or a 'get' access policy) and check the vault's network rules."),
            0 => Fail(reference, ErrorKind.ProviderUnavailable, $"Key Vault could not be reached (status 0: no HTTP response){code}."),
            _ => Fail(reference, ErrorKind.ProviderUnavailable, $"Key Vault request failed with HTTP status {exception.Status}{code}."),
        };
    }

    private static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static Result<SecretValue> Fail(SecretReference reference, ErrorKind kind, string detail) =>
        Result<SecretValue>.Failure(new Error(kind, reference.Path, detail));
}
