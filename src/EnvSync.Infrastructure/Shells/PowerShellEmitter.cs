using System.Buffers;
using System.Globalization;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Shells;

/// <summary>
/// <c>$env:NAME = 'value'</c> for Windows PowerShell 5.1 and PowerShell 7. The script is <strong>pure printable ASCII</strong>,
/// for two reasons that both come from how <c>envsync env | Invoke-Expression</c> works:
/// <list type="bullet">
/// <item>
/// PowerShell decodes a native command's stdout with the console's code page (437, 850, 1252...), not UTF-8. Any byte above
/// 0x7F can therefore be silently changed on the way in, and under some code pages a byte pair can even turn into a quote
/// character that ends the string literal. ASCII is the one thing every code page agrees on.
/// </item>
/// <item>
/// <c>Invoke-Expression</c> evaluates its input <em>one line at a time</em>, so a raw line break inside a value would cut the
/// statement in half. Every statement stays on a single line.
/// </item>
/// </list>
/// Printable ASCII goes inside single quotes, where nothing is interpreted (only <c>'</c> is doubled). Every other character,
/// including line breaks and each half of a surrogate pair, is written as <c>' + [char]N + '</c> with its UTF-16 code unit.
/// </summary>
public sealed class PowerShellEmitter : IShellEmitter
{
    private const string Prefix = "$env:";
    private const string Assignment = " = '";
    private const string EncodedStart = "' + [char]";
    private const string EncodedEnd = " + '";

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
            if (c == '\'')
            {
                cursor.Append('\'');
                cursor.Append('\'');
            }
            else if (IsPrintableAscii(c))
            {
                cursor.Append(c);
            }
            else
            {
                cursor.Append(EncodedStart);
                cursor.AppendCode(c);
                cursor.Append(EncodedEnd);
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
        var length = 0;

        foreach (var c in value)
        {
            length += c == '\'' ? 2
                : IsPrintableAscii(c) ? 1
                : EncodedStart.Length + DecimalDigits(c) + EncodedEnd.Length;
        }

        return length;
    }

    private static bool IsPrintableAscii(char c) => c is >= ' ' and <= '~';

    private static int DecimalDigits(char c) => c switch
    {
        < (char)10 => 1,
        < (char)100 => 2,
        < (char)1000 => 3,
        < (char)10000 => 4,
        _ => 5,
    };
}
