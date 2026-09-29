using System.CommandLine;
using System.Globalization;

namespace EnvSync.Cli.Commands;

/// <summary>Options every command shares. They are declared once on the root command and marked recursive.</summary>
internal sealed class GlobalOptions
{
    public Option<string?> Manifest { get; } = new("--manifest", "-m")
    {
        Description = "Path to envsync.json. Without it the file is searched upwards from the current directory.",
        Recursive = true,
    };

    public Option<string?> Profile { get; } = new("--profile", "-p")
    {
        Description = "Profile to use. Falls back to the ENVSYNC_PROFILE variable, then to 'defaultProfile' in the manifest.",
        Recursive = true,
    };

    public Option<int> Timeout { get; } = PositiveInteger(
        "--timeout",
        "Seconds to wait for each secret before giving up on it.",
        defaultValue: 15,
        meaning: "a whole number of seconds");

    public Option<int> Concurrency { get; } = PositiveInteger(
        "--concurrency",
        "How many secrets are fetched at the same time.",
        defaultValue: 8,
        meaning: "a whole number of secrets");

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Also report which profile is used and how long resolving took. Values are never shown.",
        Recursive = true,
    };

    public IEnumerable<Option> All => [Manifest, Profile, Timeout, Concurrency, Verbose];

    /// <summary>
    /// Validated inside the parser, where the value is still the text the user typed. A validator would run after a failed
    /// conversion and have nothing valid to read.
    /// </summary>
    private static Option<int> PositiveInteger(string name, string description, int defaultValue, string meaning) => new(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue,
        CustomParser = result =>
        {
            if (result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value >= 1)
            {
                return value;
            }

            result.AddError($"{name} must be {meaning}, 1 or more.");
            return defaultValue;
        },
        Recursive = true,
    };
}
