using EnvSync.Providers.Aws;

namespace EnvSync.Providers.Aws.Tests;

/// <summary>Gateway double: answers from a delegate, remembers the ids it was asked for and whether it was disposed.</summary>
internal sealed class FakeGateway : ISecretsManagerGateway, IDisposable
{
    private readonly Func<string, CancellationToken, Task<string?>> _respond;

    public FakeGateway(Func<string, CancellationToken, Task<string?>> respond) => _respond = respond;

    public List<string> Requested { get; } = [];

    public bool IsDisposed { get; private set; }

    public static FakeGateway Returning(string? secretString) => new((_, _) => Task.FromResult(secretString));

    public static FakeGateway Throwing(Exception exception) => new((_, _) => throw exception);

    public Task<string?> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
    {
        Requested.Add(secretId);
        return _respond(secretId, cancellationToken);
    }

    public void Dispose() => IsDisposed = true;
}
