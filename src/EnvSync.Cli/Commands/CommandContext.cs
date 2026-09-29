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

    public CommandContext(CliEnvironment host, CliServices services, GlobalOptions options, TextWriter standardOutput)
    {
        Host = host;
        Services = services;
        StandardOutput = standardOutput;
        _options = options;
    }

    public CliEnvironment Host { get; }

    public CliServices Services { get; }

    public TextWriter StandardOutput { get; }

    public TextWriter StandardError => Host.StandardError;

    public CommonSettings Read(ParseResult parse) => new(
        parse.GetValue(_options.Manifest),
        parse.GetValue(_options.Profile) ?? Host.GetEnvironmentVariable(ProfileVariable),
        new ResolveOptions(parse.GetValue(_options.Concurrency), TimeSpan.FromSeconds(parse.GetValue(_options.Timeout))),
        parse.GetValue(_options.Verbose));

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
        if (settings.Verbose)
        {
            StandardError.WriteLine($"envsync: using profile '{profile.Name}' ({profile.Variables.Length} variable(s) from {profile.Providers.Length} provider(s)).");
        }

        return (profile, ExitCodes.Success);
    }
}
