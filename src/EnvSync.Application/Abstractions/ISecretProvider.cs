using EnvSync.Domain;

namespace EnvSync.Application.Abstractions;

/// <summary>
/// Reads one secret from a secret manager. Implementations report expected failures (not found, denied...)
/// as a failed <see cref="Result{T}"/> and never let an SDK exception type escape.
/// A provider that owns resources also implements <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
/// </summary>
public interface ISecretProvider
{
    ValueTask<Result<SecretValue>> GetAsync(SecretReference reference, CancellationToken cancellationToken);
}
