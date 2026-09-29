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
}
