namespace EnvSync.Infrastructure.Tests;

/// <summary>A throwaway directory that is removed, with everything in it, when the test is done.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "envsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Joins parts onto the directory and normalizes separators, so paths compare equal with what the code under test returns.</summary>
    public string Combine(params string[] parts) => System.IO.Path.GetFullPath(System.IO.Path.Combine([Path, .. parts]));

    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process that is still shutting down may hold a file; the OS temp cleaner will get it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
