using System.Buffers;
using EnvSync.Domain;

namespace EnvSync.Application.Abstractions;

/// <summary>Writes one variable assignment in the syntax of a specific shell.</summary>
public interface IShellEmitter
{
    ShellKind Shell { get; }

    /// <summary>
    /// Writes one complete statement, or nothing at all when <paramref name="value"/> cannot be represented
    /// safely in this shell (in which case it returns <see langword="false"/>). Never writes a partial statement.
    /// </summary>
    bool TryWriteVariable(EnvironmentVariableName name, ReadOnlySpan<char> value, IBufferWriter<char> output);

    /// <summary>
    /// Writes a statement that makes whoever runs the script fail with <paramref name="exitCode"/>. It is printed when the environment
    /// could not be built: <c>eval "$(envsync env)"</c> would otherwise succeed on empty output and let the caller carry on
    /// half-configured. It must stop a <c>set -e</c> script or an <c>Invoke-Expression</c> pipeline, and must not close an
    /// interactive shell.
    /// </summary>
    void WriteFailure(IBufferWriter<char> output, int exitCode);
}
