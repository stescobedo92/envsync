using System.Text.Json;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Cli.Output;

/// <summary>
/// Renders what happened to each variable. It is handed a <see cref="ResolutionResult"/> and can only show names, providers,
/// statuses and reasons: values are never reachable from here, so a report cannot leak one.
/// </summary>
internal static class ReportWriter
{
    private static readonly string[] Headers = ["VARIABLE", "PROVIDER", "STATUS", "DETAIL"];

    public static void WriteTable(TextWriter writer, string profileName, ResolutionResult result)
    {
        var rows = new List<string[]>(result.Outcomes.Length) { Headers };
        foreach (var outcome in result.Outcomes)
        {
            rows.Add([outcome.Spec.Name.Value, outcome.Spec.From, Label(outcome), Detail(outcome)]);
        }

        var widths = new int[Headers.Length - 1];
        for (var column = 0; column < widths.Length; column++)
        {
            widths[column] = rows.Max(row => row[column].Length);
        }

        writer.WriteLine($"Profile '{profileName}'");
        foreach (var row in rows)
        {
            writer.WriteLine($"{row[0].PadRight(widths[0])}  {row[1].PadRight(widths[1])}  {row[2].PadRight(widths[2])}  {row[3]}".TrimEnd());
        }

        writer.WriteLine(Summary(result));
    }

    /// <summary>The report shown when a command could not go on, followed by what that means.</summary>
    public static void WriteFailure(TextWriter writer, string profileName, ResolutionResult result, string consequence)
    {
        WriteTable(writer, profileName, result);
        writer.WriteLine($"error: {result.Errors.Length} problem(s) found; {consequence}.");
    }

    public static void WriteWarnings(TextWriter writer, ResolutionResult result)
    {
        foreach (var outcome in result.Outcomes)
        {
            if (outcome.Status == VariableStatus.Skipped)
            {
                writer.WriteLine($"warning: optional variable {outcome.Spec.Name.Value} was not found in '{outcome.Spec.From}' and was left unset.");
            }
        }
    }

    public static void WriteJson(Stream output, string profileName, ResolutionResult result, int exitCode)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteString("profile", profileName);
        json.WriteBoolean("ok", result.IsSatisfied);
        json.WriteNumber("exitCode", exitCode);
        json.WriteStartArray("variables");

        foreach (var outcome in result.Outcomes)
        {
            json.WriteStartObject();
            json.WriteString("name", outcome.Spec.Name.Value);
            json.WriteString("provider", outcome.Spec.From);
            json.WriteBoolean("required", outcome.Spec.Required);
            json.WriteString("status", outcome.Status.ToString().ToLowerInvariant());

            if (outcome.Status is VariableStatus.Missing or VariableStatus.Failed or VariableStatus.Skipped)
            {
                json.WriteStartObject("error");
                json.WriteString("kind", outcome.Error.Kind.ToString());
                json.WriteString("detail", ErrorWriter.OneLine(outcome.Error.Detail));
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        output.WriteByte((byte)'\n');
    }

    private static string Label(VariableOutcome outcome) => outcome.Status switch
    {
        VariableStatus.Resolved => "OK",
        VariableStatus.Skipped => "SKIPPED",
        VariableStatus.Missing => "MISSING",
        VariableStatus.Unverified => "UNVERIFIED",
        _ => outcome.Error.Kind switch
        {
            ErrorKind.AuthenticationFailed => "AUTH",
            ErrorKind.Timeout => "TIMEOUT",
            ErrorKind.ProviderMisconfigured or ErrorKind.ProviderUnknown or ErrorKind.ManifestInvalid or ErrorKind.FieldRequired => "CONFIG",
            _ => "ERROR",
        },
    };

    private static string Detail(VariableOutcome outcome) => outcome.Status switch
    {
        VariableStatus.Resolved => string.Empty,
        VariableStatus.Skipped => "optional, not found: left unset",
        VariableStatus.Unverified => "not checked (--offline)",
        _ => ErrorWriter.OneLine(outcome.Error.Detail),
    };

    private static string Summary(ResolutionResult result)
    {
        int ready = 0, skipped = 0, missing = 0, failed = 0, unverified = 0;
        foreach (var outcome in result.Outcomes)
        {
            switch (outcome.Status)
            {
                case VariableStatus.Resolved: ready++; break;
                case VariableStatus.Skipped: skipped++; break;
                case VariableStatus.Missing: missing++; break;
                case VariableStatus.Failed: failed++; break;
                default: unverified++; break;
            }
        }

        var text = $"{result.Outcomes.Length} variable(s): {ready} ready, {missing} missing, {failed} failed, {skipped} skipped";
        return (unverified > 0 ? $"{text}, {unverified} unverified" : text) + ".";
    }
}
