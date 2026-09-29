using System.Buffers;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.TestSupport;

/// <summary>Emitter double that writes <c>NAME=value</c> lines and can be told to refuse some values.</summary>
public sealed class FakeShellEmitter : IShellEmitter
{
    public FakeShellEmitter(ShellKind shell = ShellKind.Bash) => Shell = shell;

    public ShellKind Shell { get; }

    public Func<string, bool>? Rejects { get; init; }

    public bool TryWriteVariable(EnvironmentVariableName name, ReadOnlySpan<char> value, IBufferWriter<char> output)
    {
        if (Rejects?.Invoke(value.ToString()) == true)
        {
            return false;
        }

        Append(output, name.Value);
        Append(output, "=");
        Append(output, value);
        Append(output, "\n");
        return true;
    }

    private static void Append(IBufferWriter<char> output, ReadOnlySpan<char> text)
    {
        text.CopyTo(output.GetSpan(text.Length));
        output.Advance(text.Length);
    }
}
