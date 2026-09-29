namespace EnvSync.Providers.Aws;

/// <summary>
/// The one call the provider needs from AWS. Keeping it behind this seam lets the provider's error translation be tested
/// with the SDK's real exception types, without faking the large <c>IAmazonSecretsManager</c> interface.
/// </summary>
public interface ISecretsManagerGateway
{
    /// <returns>The secret's text, or <see langword="null"/> when the secret is binary.</returns>
    Task<string?> GetSecretStringAsync(string secretId, CancellationToken cancellationToken);
}
