using System.Text;

namespace EnvSync.Cli;

/// <summary>
/// Everything the CLI takes from the outside world, gathered in one place so the whole application can run in-process
/// against a fake terminal. <see cref="StandardOutput"/> is the raw byte stream: scripts are written to it as UTF-8
/// without a byte order mark, which is what every shell expects when its input is piped.
/// </summary>
internal sealed class CliEnvironment
{
    public required Stream StandardOutput { get; init; }

    public required TextWriter StandardError { get; init; }

    /// <summary>False when stdout is a terminal, where printing a secret would leave it in the scrollback.</summary>
    public required bool IsOutputRedirected { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public required string CurrentDirectory { get; init; }

    public static CliEnvironment FromProcess() => new()
    {
        StandardOutput = Console.OpenStandardOutput(),
        StandardError = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true },
        IsOutputRedirected = Console.IsOutputRedirected,
        GetEnvironmentVariable = Environment.GetEnvironmentVariable,
        CurrentDirectory = Directory.GetCurrentDirectory(),
    };
}
