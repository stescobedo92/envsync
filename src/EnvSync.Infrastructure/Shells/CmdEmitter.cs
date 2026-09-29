using System.Buffers;
using System.Globalization;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Shells;

/// <summary>
/// <c>set "NAME=value"</c> for cmd.exe. Inside the quotes <c>&amp; | &lt; &gt; ( )</c> stay literal, but cmd offers no escape
/// that is correct in every context for the rest, so this emitter does not guess: it accepts only printable ASCII
/// and refuses <c>% " ! ^</c> (variable expansion, quote termination, delayed expansion, escape). ASCII only, because the
/// batch file is decoded with whatever OEM code page is active. A refused value is reported, never mangled.
/// </summary>
public sealed class CmdEmitter : IShellEmitter
{
    private const string SetKeyword = "set \"";

    public ShellKind Shell => ShellKind.Cmd;

    public bool TryWriteVariable(EnvironmentVariableName name, ReadOnlySpan<char> value, IBufferWriter<char> output)
    {
        foreach (var c in value)
        {
            if (!IsSafe(c))
            {
                return false;
            }
        }

        var length = SetKeyword.Length + name.Value.Length + 1 + value.Length + 3;

        var cursor = new SpanCursor(output.GetSpan(length));
        cursor.Append(SetKeyword);
        cursor.Append(name.Value);
        cursor.Append('=');
        cursor.Append(value);
        cursor.Append("\"\r\n");
        output.Advance(cursor.Length);
        return true;
    }

    /// <summary><c>cmd /c exit N</c> sets the error level without closing the console, which a bare <c>exit</c> would.</summary>
    public void WriteFailure(IBufferWriter<char> output, int exitCode) =>
        output.Write(string.Create(CultureInfo.InvariantCulture, $"cmd /c exit {exitCode}\r\n"));

    private static bool IsSafe(char c) => c is >= ' ' and <= '~' and not ('%' or '"' or '!' or '^');
}
