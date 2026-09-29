using System.Collections.Immutable;
using System.CommandLine;
using EnvSync.Application.Run;
using EnvSync.Cli.Output;

namespace EnvSync.Cli.Commands;

/// <summary><c>envsync run -- program args...</c>: start a program with the secrets in its own environment and nowhere else.</summary>
internal sealed class RunCommand
{
    private readonly CommandContext _context;

    private readonly Argument<string[]> _program = new("command")
    {
        Description = "The program to run, followed by its arguments. Put -- before it so its options are not read as envsync's.",
        Arity = ArgumentArity.OneOrMore,
    };

    public RunCommand(CommandContext context) => _context = context;

    public Command Build()
    {
        var command = new Command("run", "Run a program with the secrets injected into its environment only. Nothing is written to disk.")
        {
            _program,
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

        var words = parse.GetValue(_program)!;
        var request = new RunProcessCommand(
            profile,
            settings.Resolve,
            words[0],
            ImmutableArray.Create(words, 1, words.Length - 1),
            Resolved: resolution => ReportWriter.WriteWarnings(_context.StandardError, resolution));

        var result = await _context.Services.Run.HandleAsync(request, cancellationToken);

        if (!result.Resolution.IsSatisfied)
        {
            ReportWriter.WriteFailure(_context.StandardError, profile.Name, result.Resolution, "the program was not started");
        }
        else if (result.LaunchError is { } launchError)
        {
            ErrorWriter.Write(_context.StandardError, [launchError]);
        }

        return result.ExitCode;
    }
}
