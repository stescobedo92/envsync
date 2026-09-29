using System.CommandLine;
using EnvSync.Application;
using EnvSync.Application.Requirements;
using EnvSync.Cli.Output;

namespace EnvSync.Cli.Commands;

/// <summary>
/// <c>envsync check</c>: is every key this profile needs available? Secrets are fetched to prove they exist and are dropped on
/// the spot; the report can only ever contain names and reasons. The exit code makes it usable in CI and pre-commit hooks.
/// </summary>
internal sealed class CheckCommand
{
    private const string Text = "text";
    private const string Json = "json";

    private readonly CommandContext _context;

    private readonly Option<bool> _offline = new("--offline")
    {
        Description = "Only validate the manifest and the providers' settings; make no request.",
    };

    private readonly Option<string> _format = new("--format", "-f")
    {
        Description = "Report format: text (default) or json, for CI.",
        DefaultValueFactory = static _ => Text,
    };

    public CheckCommand(CommandContext context)
    {
        _context = context;
        _format.Validators.Add(static result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!string.Equals(value, Text, StringComparison.OrdinalIgnoreCase) && !string.Equals(value, Json, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError($"--format must be {Text} or {Json} (got '{value}').");
            }
        });
    }

    public Command Build()
    {
        var command = new Command("check", "Check that every key the profile needs is available, without printing any value. Exit code 0 means ready.")
        {
            _offline,
            _format,
        };

        command.SetAction(ExecuteAsync);
        return command;
    }

    private async Task<int> ExecuteAsync(ParseResult parse, CancellationToken cancellationToken)
    {
        var settings = _context.Read(parse);
        var (profile, exitCode) = await _context.LoadProfileAsync(settings, cancellationToken);
        if (profile is null)
        {
            return exitCode;
        }

        var result = await _context.Services.Check.HandleAsync(
            new CheckRequirementsQuery(profile, settings.Resolve, parse.GetValue(_offline)),
            cancellationToken);

        exitCode = ExitCodes.For(result);

        if (string.Equals(parse.GetValue(_format), Json, StringComparison.OrdinalIgnoreCase))
        {
            await _context.StandardOutput.FlushAsync(cancellationToken);
            ReportWriter.WriteJson(_context.Host.StandardOutput, profile.Name, result, exitCode);
        }
        else
        {
            ReportWriter.WriteTable(_context.StandardOutput, profile.Name, result);
        }

        return exitCode;
    }
}
