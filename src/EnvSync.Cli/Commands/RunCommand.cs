using System.Collections.Immutable;
using System.CommandLine;
using EnvSync.Application;
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
        var command = new Command("run", "Run a program with the secrets injected into its environment only. Nothing is written to disk. Always put -- before the program.")
        {
            _program,
        };

        command.SetAction(ExecuteAsync);
        return command;
    }

    private async Task<int> ExecuteAsync(ParseResult parse, CancellationToken cancellationToken)
    {
        // Without --, -v, -p and -m anywhere after the program are read as envsync's own options and quietly removed from the
        // child's arguments. Requiring the separator makes "which arguments are the program's" unambiguous.
        if (!_context.HasDoubleDash)
        {
            _context.StandardError.WriteLine(
                "error: put -- between envsync's options and the program, for example: envsync run -- dotnet run. " +
                "Otherwise options such as -v, -p and -m after the program would be taken as envsync's.");
            return ExitCodes.UsageError;
        }

        if (!_context.TryRead(parse, out var settings))
        {
            return ExitCodes.UsageError;
        }

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
            Resolved: resolution => ReportWriter.WriteWarnings(_context.StandardError, resolution, inheritedValuesRemain: false));

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
