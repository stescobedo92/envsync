using System.Text;

namespace EnvSync.Application.Diagnostics;

/// <summary>Turns text that came from a third party (an SDK message, a server reply) into something safe to put in one line of output.</summary>
public static class ErrorText
{
    /// <summary>
    /// Every run of whitespace or control characters becomes one space, so the text stays on one line and cannot carry terminal
    /// escape sequences; anything longer than <paramref name="maxLength"/> is cut and marked with <c>...</c>. Unlike keeping only the
    /// first line, nothing that explains the failure is thrown away: SDKs put the useful part on the later lines.
    /// </summary>
    public static string Summarize(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 3));
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
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
}
