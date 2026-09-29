using System.Buffers;
using System.CommandLine;
using EnvSync.Application;
using EnvSync.Application.Resolution;
using EnvSync.Cli.Output;
using EnvSync.Domain;

namespace EnvSync.Cli.Commands;

internal sealed record CommonSettings(string? ManifestPath, string? ProfileName, ResolveOptions Resolve, bool Verbose);

/// <summary>What every command needs: the terminal, the wired handlers, the shared options and the shared "load my profile" step.</summary>
internal sealed class CommandContext
{
    private const string ProfileVariable = "ENVSYNC_PROFILE";

    private readonly GlobalOptions _options;

    public CommandContext(CliEnvironment host, CliServices services, GlobalOptions options, TextWriter standardOutput, bool hasDoubleDash)
    {
        Host = host;
        Services = services;
        StandardOutput = standardOutput;
        HasDoubleDash = hasDoubleDash;
        _options = options;
    }

    public CliEnvironment Host { get; }

    public CliServices Services { get; }

    public TextWriter StandardOutput { get; }

    public TextWriter StandardError => Host.StandardError;

    /// <summary>Whether the command line contained a <c>--</c>, which <c>run</c> needs to tell its own options from the program's.</summary>
    public bool HasDoubleDash { get; }

    /// <summary>Reads the shared options. Returns false, after saying why, when the environment holds a value that must not be guessed at.</summary>
    public bool TryRead(ParseResult parse, out CommonSettings settings)
    {
        var explicitProfile = parse.GetValue(_options.Profile);
        var fromEnvironment = Host.GetEnvironmentVariable(ProfileVariable);

        // An empty ENVSYNC_PROFILE is what a CI step produces from an unset STAGE. Falling back to the manifest's default, which
        // may well be production, would run the wrong environment without a word.
        if (explicitProfile is null && fromEnvironment is not null && string.IsNullOrWhiteSpace(fromEnvironment))
        {
            StandardError.WriteLine($"error: the {ProfileVariable} environment variable is set but empty: unset it, or give it a profile name.");
            settings = default!;
            return false;
        }

        settings = new CommonSettings(
            parse.GetValue(_options.Manifest),
            explicitProfile ?? fromEnvironment,
            new ResolveOptions(parse.GetValue(_options.Concurrency), TimeSpan.FromSeconds(parse.GetValue(_options.Timeout))),
            parse.GetValue(_options.Verbose));
        return true;
    }

    /// <summary>Loads the manifest and picks the profile, reporting every problem found. The exit code is set when there is no profile.</summary>
    public async ValueTask<(Profile? Profile, int ExitCode)> LoadProfileAsync(CommonSettings settings, CancellationToken cancellationToken)
    {
        var manifest = await Services.ManifestLoader.LoadAsync(settings.ManifestPath, Host.CurrentDirectory, cancellationToken);
        if (!manifest.IsSuccess)
        {
            ErrorWriter.Write(StandardError, manifest.Errors);
            return (null, ExitCodes.For(manifest.Errors));
        }

        var selected = manifest.Value.SelectProfile(settings.ProfileName);
        if (!selected.IsSuccess)
        {
            ErrorWriter.Write(StandardError, selected.Errors);
            return (null, ExitCodes.For(selected.Errors));
        }

        var profile = selected.Value;
        var name = ErrorWriter.Safe(profile.Name);

        if (settings.ProfileName is null)
        {
            // Chosen for the user, not by them: say so, because a default is easily mistaken for a choice.
            var why = string.Equals(manifest.Value.DefaultProfile, profile.Name, StringComparison.Ordinal)
                ? "the manifest's defaultProfile"
                : "the only profile in the manifest";
            StandardError.WriteLine($"envsync: using profile '{name}' ({why}).");
        }

        if (settings.Verbose)
        {
            StandardError.WriteLine($"envsync: profile '{name}' has {profile.Variables.Length} variable(s) from {profile.Providers.Length} provider(s).");
        }

        return (profile, ExitCodes.Success);
    }

    /// <summary>
    /// stdout is buffered and stderr is not, so without this a message on stderr can appear <em>before</em> the report it refers to.
    /// A closed pipe is ignored: the exit code matters more than a report nobody is reading.
    /// </summary>
    public async ValueTask FlushStandardOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StandardOutput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // Nobody is reading stdout any more.
        }
    }

    /// <summary>The statement that makes whoever evaluates <c>env</c>'s output fail with <paramref name="exitCode"/>.</summary>
    public string FailureScript(ShellKind shell, int exitCode)
    {
        foreach (var emitter in Services.ShellEmitters)
        {
            if (emitter.Shell == shell)
            {
                var writer = new ArrayBufferWriter<char>();
                emitter.WriteFailure(writer, exitCode);
                return writer.WrittenSpan.ToString();
            }
        }

        return string.Empty;
    }
}
