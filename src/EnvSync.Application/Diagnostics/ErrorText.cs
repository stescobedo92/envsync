using System.Globalization;
using System.Text;

namespace EnvSync.Application.Diagnostics;

/// <summary>
/// Turns text that did not come from envsync (a manifest, an SDK message, a server reply) into something safe to print. Such text
/// is attacker-influenced: a profile name, an alias or an error message can carry terminal escape sequences that erase lines,
/// line breaks that forge a status line or a CI workflow command, or bidirectional overrides that make output lie.
/// </summary>
public static class ErrorText
{
    /// <summary>
    /// Replaces every control character and every invisible formatting or separator character (ESC, line and paragraph separators,
    /// bidirectional overrides, zero-width joiners...) with <c>?</c>. Everything else, accented and non-Latin text included, is
    /// left alone, and the same instance comes back when there is nothing to change.
    /// </summary>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var first = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsUnsafe(text[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IsUnsafe(source[i]) ? '?' : source[i];
            }
        });
    }

    /// <summary>
    /// Every run of whitespace or unsafe characters becomes one space, so the text stays on one line and cannot forge output;
    /// anything longer than <paramref name="maxLength"/> is cut and marked with <c>...</c>. Unlike keeping only the first line,
    /// nothing that explains the failure is thrown away: SDKs put the useful part on the later lines.
    /// </summary>
    public static string Summarize(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 3));
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || IsUnsafe(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length == maxLength)
            {
                return builder.Append("...").ToString();
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsUnsafe(char c)
    {
        if (char.IsControl(c))
        {
            return true;
        }

        return c > '\u007f'
            && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
