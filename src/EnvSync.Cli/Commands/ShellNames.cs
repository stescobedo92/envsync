using EnvSync.Domain;

namespace EnvSync.Cli.Commands;

/// <summary>The names a user can type for <c>--shell</c>, mapped to the emitter that serves them.</summary>
internal static class ShellNames
{
    public const string Choices = "pwsh, powershell, bash, zsh, cmd";

    /// <summary>The shell most users of this operating system have: PowerShell on Windows, bash elsewhere.</summary>
    public static ShellKind Default => OperatingSystem.IsWindows() ? ShellKind.PowerShell : ShellKind.Bash;

    public static bool TryParse(string? text, out ShellKind shell)
    {
        if (text is not null)
        {
            if (Is(text, "pwsh") || Is(text, "powershell"))
            {
                shell = ShellKind.PowerShell;
                return true;
            }

            if (Is(text, "bash") || Is(text, "zsh"))
            {
                shell = ShellKind.Bash;
                return true;
            }

            if (Is(text, "cmd"))
            {
                shell = ShellKind.Cmd;
                return true;
            }
        }

        shell = default;
        return false;
    }

    private static bool Is(string text, string name) => string.Equals(text, name, StringComparison.OrdinalIgnoreCase);
}
