namespace EnvSync.Application.Providers;

/// <summary>
/// Hosts the <em>user</em> has declared trustworthy, through the <c>ENVSYNC_TRUSTED_HOSTS</c> environment variable and never through
/// the manifest. The point of the separation is that a manifest is repository content anyone with write access can change, while
/// the environment belongs to the person running the tool: only the latter may decide where a credential is allowed to go.
/// Entries are exact hosts or <c>*.suffix</c> wildcards, separated by commas, semicolons or whitespace. A wildcard matches
/// subdomains only, never the bare domain, and a lone <c>*</c> matches nothing.
/// </summary>
public sealed class TrustedHosts
{
    public const string VariableName = "ENVSYNC_TRUSTED_HOSTS";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    private readonly HashSet<string> _exact;
    private readonly string[] _suffixes;

    private TrustedHosts(HashSet<string> exact, string[] suffixes)
    {
        _exact = exact;
        _suffixes = suffixes;
    }

    public static TrustedHosts FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return Parse(getEnvironmentVariable(VariableName));
    }

    public static TrustedHosts Parse(string? list)
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suffixes = new List<string>();

        foreach (var entry in (list ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entry[1..];
                if (suffix.Length > 1 && suffix.Trim('.').Length > 0)
                {
                    suffixes.Add(suffix);
                }
            }
            else if (entry.Trim('.', '*').Length > 0 && !entry.Contains('*', StringComparison.Ordinal))
            {
                exact.Add(entry);
            }
        }

        return new TrustedHosts(exact, [.. suffixes]);
    }

    public bool IsTrusted(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_exact.Contains(host))
        {
            return true;
        }

        foreach (var suffix in _suffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
