using System.Buffers;
using System.Globalization;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Shells;

/// <summary>
/// <c>export NAME='value'</c> for bash and zsh. Inside single quotes nothing is interpreted, so two characters need care:
/// <list type="bullet">
/// <item>The single quote itself, written as <c>'\''</c> (close, escaped quote, reopen).</item>
/// <item>
/// The carriage return, written as the ANSI-C escape <c>$'\r'</c>. Cygwin/MSYS bash (Git Bash on Windows) drops a raw CR while
/// reading a script even inside quotes; the escape is plain ASCII, so it survives every way of reading the script.
/// Every other control character was checked and survives as is.
/// </item>
/// </list>
/// </summary>
public sealed class BashEmitter : IShellEmitter
{
    private const string ExportKeyword = "export ";
    private const string EscapedQuote = "'\\''";
    private const string EscapedCarriageReturn = "'$'\\r''";

    public ShellKind Shell => ShellKind.Bash;

    public bool TryWriteVariable(EnvironmentVariableName name, ReadOnlySpan<char> value, IBufferWriter<char> output)
    {
        // A NUL cannot exist in an environment variable, and would silently truncate the value.
        if (value.Contains('\0'))
        {
            return false;
        }

        var quotes = value.Count('\'');
        var carriageReturns = value.Count('\r');
        var length = ExportKeyword.Length + name.Value.Length + 2 + value.Length
            + (quotes * (EscapedQuote.Length - 1))
            + (carriageReturns * (EscapedCarriageReturn.Length - 1))
            + 2;

        var cursor = new SpanCursor(output.GetSpan(length));
        cursor.Append(ExportKeyword);
        cursor.Append(name.Value);
        cursor.Append("='");

        foreach (var c in value)
        {
            if (c == '\'')
            {
                cursor.Append(EscapedQuote);
            }
            else if (c == '\r')
            {
                cursor.Append(EscapedCarriageReturn);
            }
            else
            {
                cursor.Append(c);
            }
        }

        cursor.Append("'\n");
        output.Advance(cursor.Length);
        return true;
    }

    /// <summary><c>(exit N)</c> runs in a subshell: it fails under <c>set -e</c> without closing an interactive session.</summary>
    public void WriteFailure(IBufferWriter<char> output, int exitCode) =>
        output.Write(string.Create(CultureInfo.InvariantCulture, $"(exit {exitCode})\n"));
}
