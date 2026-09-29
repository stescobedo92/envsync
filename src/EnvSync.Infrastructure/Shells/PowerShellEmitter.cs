using System.Buffers;
using System.Globalization;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Shells;

/// <summary>
/// <c>$env:NAME = 'value'</c> for Windows PowerShell 5.1 and PowerShell 7. Single-quoted strings interpret nothing, with two
/// traps this emitter closes:
/// <list type="bullet">
/// <item>PowerShell treats the typographic quotes U+2018, U+2019, U+201A and U+201B exactly like <c>'</c>, so each is doubled too.</item>
/// <item>
/// <c>envsync env | Invoke-Expression</c> evaluates its input <em>one line at a time</em>, so a raw line break inside a value
/// would cut the statement in half. Line breaks are therefore written as <c>[char]10</c> / <c>[char]13</c> and every
/// statement stays on a single line.
/// </item>
/// </list>
/// </summary>
public sealed class PowerShellEmitter : IShellEmitter
{
    private const string Prefix = "$env:";
    private const string Assignment = " = '";
    private const string LineFeed = "' + [char]10 + '";
    private const string CarriageReturn = "' + [char]13 + '";

    public ShellKind Shell => ShellKind.PowerShell;

    public bool TryWriteVariable(EnvironmentVariableName name, ReadOnlySpan<char> value, IBufferWriter<char> output)
    {
        if (value.Contains('\0'))
        {
            return false;
        }

        var length = Prefix.Length + name.Value.Length + Assignment.Length + ExpandedLength(value) + 2;

        var cursor = new SpanCursor(output.GetSpan(length));
        cursor.Append(Prefix);
        cursor.Append(name.Value);
        cursor.Append(Assignment);

        foreach (var c in value)
        {
            if (IsSingleQuote(c))
            {
                cursor.Append(c);
                cursor.Append(c);
            }
            else if (c == '\n')
            {
                cursor.Append(LineFeed);
            }
            else if (c == '\r')
            {
                cursor.Append(CarriageReturn);
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

    /// <summary>A terminating error: it stops an <c>Invoke-Expression</c> pipeline and a script running with errors set to Stop.</summary>
    public void WriteFailure(IBufferWriter<char> output, int exitCode) =>
        output.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"throw 'envsync: could not set the environment (exit code {exitCode}); see the messages above.'\n"));

    private static int ExpandedLength(ReadOnlySpan<char> value)
    {
        var length = value.Length;

        foreach (var c in value)
        {
            if (IsSingleQuote(c))
            {
                length++;
            }
            else if (c is '\n' or '\r')
            {
                length += LineFeed.Length - 1;
            }
        }

        return length;
    }

    private static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';
}
