namespace EnvSync.Infrastructure.Manifests;

/// <summary>Finds <c>envsync.json</c> by walking up from a directory, the way git finds <c>.git</c>.</summary>
public static class ManifestLocator
{
    public const string FileName = "envsync.json";

    /// <returns>The full path of the nearest manifest, or <see langword="null"/> when none exists up to the filesystem root.</returns>
    public static string? FindUpwards(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
