using System.Net;
using Amazon.Runtime;
using Amazon.SecretsManager.Model;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Diagnostics;
using EnvSync.Application.Secrets;
using EnvSync.Domain;

namespace EnvSync.Providers.Aws;

/// <summary>Reads a secret's text from AWS Secrets Manager and translates the SDK's exceptions into typed errors.</summary>
public sealed class AwsSecretsProvider : ISecretProvider, IDisposable
{
    private const int MaxNameLength = 512;
    private const int MaxArnLength = 2048;
    private const int MaxDetailLength = 600;

    private static readonly HashSet<string> AuthenticationCodes = new(StringComparer.Ordinal)
    {
        "AccessDeniedException", "AccessDenied", "UnrecognizedClientException", "ExpiredTokenException", "ExpiredToken",
        "InvalidSignatureException", "InvalidClientTokenId", "SignatureDoesNotMatch", "InvalidToken", "IncompleteSignature",
        "MissingAuthenticationToken", "AuthFailure",
    };

    private readonly ISecretsManagerGateway _gateway;

    public AwsSecretsProvider(ISecretsManagerGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    public async ValueTask<Result<SecretValue>> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (!IsValidSecretId(reference.Path))
        {
            return Fail(
                reference,
                ErrorKind.ManifestInvalid,
                $"'{reference.Path}' is not a valid Secrets Manager secret id: use letters, digits and / _ + = . @ - (up to 512 characters), or a full ARN.");
        }

        try
        {
            var text = await _gateway.GetSecretStringAsync(reference.Path, cancellationToken);
            return text is null
                ? Fail(reference, ErrorKind.UnsupportedSecretType, $"Secret '{reference.Path}' is binary; store it as text (a plain string or JSON) so it can be used as an environment variable.")
                : StructuredSecret.Select(text, reference);
        }
        catch (ResourceNotFoundException)
        {
            return Fail(reference, ErrorKind.SecretNotFound, $"Secret '{reference.Path}' was not found in this account and region.");
        }
        catch (InvalidRequestException)
        {
            return Fail(reference, ErrorKind.SecretNotFound, $"Secret '{reference.Path}' cannot be read right now; it may be scheduled for deletion.");
        }
        catch (InvalidParameterException)
        {
            return Fail(reference, ErrorKind.ManifestInvalid, $"AWS rejected '{reference.Path}' as a secret id.");
        }
        catch (DecryptionFailureException)
        {
            return Fail(reference, ErrorKind.AuthenticationFailed, $"Secrets Manager could not decrypt '{reference.Path}': check that the identity may use the KMS key that protects it.");
        }
        catch (AmazonServiceException exception)
        {
            return Translate(exception, reference);
        }
        catch (AmazonClientException exception)
        {
            return TranslateClient(exception, reference);
        }
    }

    public void Dispose() => (_gateway as IDisposable)?.Dispose();

    private static Result<SecretValue> Translate(AmazonServiceException exception, SecretReference reference)
    {
        var status = (int)exception.StatusCode;
        var code = exception.ErrorCode ?? string.Empty;

        if (AuthenticationCodes.Contains(code) || exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Fail(
                reference,
                ErrorKind.AuthenticationFailed,
                $"AWS rejected the request ({code}, HTTP {status}): {ErrorText.Summarize(exception.Message, MaxDetailLength)} Check the credentials and the secretsmanager:GetSecretValue permission.");
        }

        return Fail(
            reference,
            ErrorKind.ProviderUnavailable,
            $"AWS Secrets Manager request failed ({code}, HTTP {status}): {ErrorText.Summarize(exception.Message, MaxDetailLength)}");
    }

    private static Result<SecretValue> TranslateClient(AmazonClientException exception, SecretReference reference) =>
        exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            ? Fail(
                reference,
                ErrorKind.AuthenticationFailed,
                $"No AWS credentials were found: {ErrorText.Summarize(exception.Message, MaxDetailLength)} Run 'aws configure' or 'aws sso login', or set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY.")
            : Fail(
                reference,
                ErrorKind.ProviderUnavailable,
                $"Could not complete the request to AWS Secrets Manager: {ErrorText.Summarize(exception.Message, MaxDetailLength)}");

    private static bool IsValidSecretId(string id)
    {
        var limit = id.StartsWith("arn:", StringComparison.Ordinal) ? MaxArnLength : MaxNameLength;
        if (id.Length is 0 || id.Length > limit)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '+' or '=' or '.' or '@' or '-' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    private static Result<SecretValue> Fail(SecretReference reference, ErrorKind kind, string detail) =>
        Result<SecretValue>.Failure(new Error(kind, reference.Path, detail));
}
