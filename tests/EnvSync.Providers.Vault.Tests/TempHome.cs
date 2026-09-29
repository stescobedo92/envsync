namespace EnvSync.Providers.Vault.Tests;

/// <summary>A fake home directory, so tests can plant (or omit) a <c>~/.vault-token</c> file.</summary>
internal sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "envsync-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void WriteToken(string content) => File.WriteAllText(System.IO.Path.Combine(Path, ".vault-token"), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
