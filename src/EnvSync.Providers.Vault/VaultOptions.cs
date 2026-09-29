using EnvSync.Domain;

namespace EnvSync.Providers.Vault;

/// <param name="Address">Base address of the Vault server.</param>
/// <param name="Namespace">Vault Enterprise namespace, if any.</param>
/// <param name="Mount">Where the KV engine is mounted (may have several segments, e.g. <c>team/kv</c>).</param>
/// <param name="KvVersion">1 or 2.</param>
/// <param name="Token">Held as a <see cref="SecretValue"/>, so printing these options never leaks it.</param>
public sealed record VaultOptions(Uri Address, string? Namespace, string Mount, int KvVersion, SecretValue Token);
