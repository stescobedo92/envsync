namespace EnvSync.Infrastructure.Processes;

/// <summary>
/// Finds the file a command name refers to, the way a shell does. This exists because <c>Process.Start("npm")</c> fails on
/// Windows: <c>CreateProcess</c> only appends <c>.exe</c>, so shims such as <c>npm.cmd</c> are never found.
/// With <c>PATHEXT</c> extensions in force (Windows) a bare name is tried with each extension in order; without them (Unix) the
/// name is used as is and the file must carry an execute permission bit.
/// </summary>
public sealed class ExecutableResolver
{
    private static readonly string[] DefaultWindowsExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    private readonly string[] _directories;
    private readonly string[] _extensions;

    public ExecutableResolver(string? pathVariable, IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        _directories = string.IsNullOrEmpty(pathVariable)
            ? []
            : pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _extensions = [.. extensions.Where(static e => e.Length > 0)];
    }

    public static ExecutableResolver ForCurrentProcess()
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Join(';', DefaultWindowsExtensions))
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new ExecutableResolver(Environment.GetEnvironmentVariable("PATH"), extensions);
    }

    /// <returns>The full path to run, or <see langword="null"/> when the command cannot be found.</returns>
    public string? Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return ResolveFile(Path.GetFullPath(command));
        }

        foreach (var directory in _directories)
        {
            if (ResolveFile(Path.Combine(directory, command)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private string? ResolveFile(string path)
    {
        foreach (var candidate in Candidates(path))
        {
            if (IsRunnable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> Candidates(string path)
    {
        if (_extensions.Length == 0 || HasKnownExtension(path))
        {
            yield return path;
            yield break;
        }

        foreach (var extension in _extensions)
        {
            yield return path + extension;
        }
    }

    private bool HasKnownExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && _extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRunnable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & Execute) != 0;
    }
}
