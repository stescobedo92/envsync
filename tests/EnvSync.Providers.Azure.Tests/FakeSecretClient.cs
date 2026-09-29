using Azure;
using Azure.Security.KeyVault.Secrets;

namespace EnvSync.Providers.Azure.Tests;

/// <summary>
/// A <see cref="SecretClient"/> double built the way the Azure SDK intends: subclass the client (it has a protected
/// parameterless constructor for exactly this) and override the virtual operation. No network, no credential.
/// </summary>
internal sealed class FakeSecretClient : SecretClient
{
    private readonly Func<string, string?, CancellationToken, Task<Response<KeyVaultSecret>>> _respond;

    public FakeSecretClient(Func<string, string?, CancellationToken, Task<Response<KeyVaultSecret>>> respond) =>
        _respond = respond;

    public List<(string Name, string? Version)> Calls { get; } = [];

    public static FakeSecretClient Returning(string value) =>
        new((name, _, _) => Task.FromResult(Secret(name, value)));

    public static FakeSecretClient Throwing(Exception exception) =>
        new((_, _, _) => throw exception);

    public override Task<Response<KeyVaultSecret>> GetSecretAsync(
        string name, string? version = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((name, version));
        return _respond(name, version, cancellationToken);
    }

    private static Response<KeyVaultSecret> Secret(string name, string value) =>
        Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties(name), value), null!);
}
