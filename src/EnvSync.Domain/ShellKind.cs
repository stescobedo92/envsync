namespace EnvSync.Domain;

/// <summary>The shell dialects a session script can be generated for.</summary>
public enum ShellKind
{
    /// <summary>POSIX-style: bash and zsh.</summary>
    Bash,
    PowerShell,
    Cmd,
}
