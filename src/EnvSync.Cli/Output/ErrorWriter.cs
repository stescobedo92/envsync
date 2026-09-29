using System.Collections.Immutable;
using EnvSync.Application.Diagnostics;
using EnvSync.Domain;

namespace EnvSync.Cli.Output;

internal static class ErrorWriter
{
    /// <summary>One line per error: <c>error: subject: detail</c>. Everything is single-line and free of control characters.</summary>
    public static void Write(TextWriter writer, ImmutableArray<Error> errors)
    {
        foreach (var error in errors)
        {
            writer.WriteLine(error.Subject.Length == 0
                ? $"error: {Safe(error.Detail)}"
                : $"error: {Safe(error.Subject)}: {Safe(error.Detail)}");
        }
    }

    /// <summary>
    /// Text from a manifest or a provider is not ours: line breaks become spaces, and escape sequences, separators and bidirectional
    /// overrides become <c>?</c>, so it can neither erase a line nor forge one in a terminal or a CI log.
    /// </summary>
    public static string Safe(string text) => ErrorText.Sanitize(OneLine(text));

    public static string OneLine(string text) =>
        text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
            ? text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')
            : text;
}
