namespace EnvSync.Application.Resolution;

/// <param name="MaxConcurrency">How many secrets are fetched at once. Values below 1 mean one at a time.</param>
/// <param name="Timeout">Per-secret time budget. A non-positive value means no timeout.</param>
public readonly record struct ResolveOptions(int MaxConcurrency, TimeSpan Timeout)
{
    public static ResolveOptions Default { get; } = new(8, TimeSpan.FromSeconds(15));
}
