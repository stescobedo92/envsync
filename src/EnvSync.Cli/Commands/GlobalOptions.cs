using System.CommandLine;
using System.Globalization;

namespace EnvSync.Cli.Commands;

/// <summary>Options every command shares. They are declared once on the root command and marked recursive.</summary>
internal sealed class GlobalOptions
{
    public const int MaxTimeoutSeconds = 24 * 60 * 60;
    public const int MaxConcurrency = 256;

    public GlobalOptions()
    {
        // An empty value is never "not given": silently falling back to a default profile or an ancestor's manifest could point a
        // command at the wrong environment.
        Manifest.Validators.Add(static result => RejectBlank(result.GetValueOrDefault<string?>(), "--manifest", result.AddError));
        Profile.Validators.Add(static result => RejectBlank(result.GetValueOrDefault<string?>(), "--profile", result.AddError));
    }

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

    public Option<int> Timeout { get; } = BoundedInteger(
        "--timeout",
        "Seconds to wait for each secret before giving up on it.",
        defaultValue: 15,
        maximum: MaxTimeoutSeconds,
        meaning: "a whole number of seconds");

    public Option<int> Concurrency { get; } = BoundedInteger(
        "--concurrency",
        "How many secrets are fetched at the same time.",
        defaultValue: 8,
        maximum: MaxConcurrency,
        meaning: "a whole number of secrets");

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Also report which profile is used. Values are never shown.",
        Recursive = true,
    };

    public IEnumerable<Option> All => [Manifest, Profile, Timeout, Concurrency, Verbose];

    private static void RejectBlank(string? value, string name, Action<string> addError)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            addError($"{name} must not be empty: leave it out to use the default.");
        }
    }

    /// <summary>
    /// Validated inside the parser, where the value is still the text the user typed. A validator would run after a failed
    /// conversion and have nothing valid to read.
    /// </summary>
    private static Option<int> BoundedInteger(string name, string description, int defaultValue, int maximum, string meaning) => new(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue,
        CustomParser = result =>
        {
            if (result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value >= 1
                && value <= maximum)
            {
                return value;
            }

            result.AddError($"{name} must be {meaning} between 1 and {maximum}.");
            return defaultValue;
        },
        Recursive = true,
    };
}
