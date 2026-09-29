using System.Collections.Frozen;

namespace EnvSync.Domain;

/// <summary>
/// Names a secret manager must never be able to set. Setting one from a value stored elsewhere lets whoever controls that value,
/// or a manifest that points somewhere they control, load code or rewire a shell: <c>PROMPT_COMMAND</c>, <c>BASH_ENV</c>,
/// <c>LD_PRELOAD</c>, <c>PATH</c>, <c>NODE_OPTIONS</c>... Matching ignores case, because Windows does.
/// </summary>
public static class ReservedVariableNames
{
    private static readonly string[] Prefixes = ["LD_", "DYLD_"];

    private static readonly FrozenSet<string> Exact = new[]
    {
        // Where programs and shells look for code.
        "PATH", "PATHEXT", "COMSPEC", "PSModulePath", "CDPATH",
        // Shell behaviour and hooks that run on their own.
        "IFS", "ENV", "BASH_ENV", "PROMPT_COMMAND", "PS0", "PS1", "PS2", "PS3", "PS4", "SHELLOPTS", "BASHOPTS", "GLOBIGNORE",
        // Runtime options that load code or change what a runtime executes.
        "NODE_OPTIONS", "PYTHONSTARTUP", "PYTHONPATH", "PYTHONHOME", "PERL5OPT", "PERL5LIB", "RUBYOPT", "RUBYLIB",
        "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "DOTNET_STARTUP_HOOKS",
        // Tools that run a command taken from the environment.
        "GIT_SSH_COMMAND",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Exact.Contains(name))
        {
            return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
