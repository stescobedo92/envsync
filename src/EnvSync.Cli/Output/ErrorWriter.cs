using System.Collections.Immutable;
using EnvSync.Domain;

namespace EnvSync.Cli.Output;

internal static class ErrorWriter
{
    /// <summary>One line per error: <c>error: subject: detail</c>. Details are single-line so they cannot break the layout.</summary>
    public static void Write(TextWriter writer, ImmutableArray<Error> errors)
    {
        foreach (var error in errors)
        {
            writer.WriteLine(error.Subject.Length == 0
                ? $"error: {OneLine(error.Detail)}"
                : $"error: {error.Subject}: {OneLine(error.Detail)}");
        }
    }

    public static string OneLine(string text) =>
        text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
            ? text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')
            : text;
}
