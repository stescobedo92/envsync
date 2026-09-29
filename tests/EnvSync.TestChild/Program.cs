// Usage: EnvSync.TestChild <outputFile> <exit=N | hang> [argument ...]
// Environment: PROBE_VARS = semicolon-separated variable names to report.
//
// Writes one line per fact so a test can see exactly what the process received:
//   PID:<id>
//   ENV:<name>=<base64 of the UTF-8 value>   (base64 keeps newlines and non-ASCII intact)
//   ARG:<base64 of the UTF-8 argument>
using System.Text;

var outputFile = args[0];
var mode = args[1];

var lines = new List<string> { $"PID:{Environment.ProcessId}" };

foreach (var name in (Environment.GetEnvironmentVariable("PROBE_VARS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
{
    var value = Environment.GetEnvironmentVariable(name);
    lines.Add($"ENV:{name}={(value is null ? "<unset>" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))}");
}

foreach (var argument in args.Skip(2))
{
    lines.Add($"ARG:{Convert.ToBase64String(Encoding.UTF8.GetBytes(argument))}");
}

File.WriteAllLines(outputFile, lines);

if (mode == "hang")
{
    Thread.Sleep(Timeout.Infinite);
}

return int.Parse(mode["exit=".Length..], System.Globalization.CultureInfo.InvariantCulture);
