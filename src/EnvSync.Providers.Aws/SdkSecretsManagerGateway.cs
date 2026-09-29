using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace EnvSync.Providers.Aws;

/// <summary>The production gateway: a thin adapter over the AWS SDK client. SDK exceptions are deliberately not caught here.</summary>
public sealed class SdkSecretsManagerGateway : ISecretsManagerGateway, IDisposable
{
    private readonly IAmazonSecretsManager _client;

    public SdkSecretsManagerGateway(IAmazonSecretsManager client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<string?> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
    {
        var response = await _client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken);
        return response.SecretString;
    }

    public void Dispose() => _client.Dispose();
}
