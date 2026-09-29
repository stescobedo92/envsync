using System.CommandLine;
using EnvSync.Application;
using EnvSync.Application.Export;
using EnvSync.Cli.Output;
using EnvSync.Domain;

namespace EnvSync.Cli.Commands;

/// <summary>
/// <c>envsync env | ...</c>: print a script that sets the variables in the current shell session. Because that script holds
/// plain secrets, it refuses to print to a terminal, where it would stay in the scrollback, unless told to.
/// <para>
/// <c>eval "$(envsync env)"</c> and <c>envsync env | Invoke-Expression</c> succeed on empty output, so when the environment cannot
/// be built the only thing printed to stdout is a statement that makes the consumer fail with the same exit code, never a partial
/// script and never a secret.
/// </para>
/// </summary>
internal sealed class EnvCommand
{
    private readonly CommandContext _context;

    private readonly Option<string?> _shell = new("--shell", "-s")
    {
        Description = $"Shell to write the script for: {ShellNames.Choices}. Defaults to PowerShell on Windows and bash elsewhere.",
    };

    private readonly Option<bool> _unsafePrint = new("--unsafe-print")
    {
        Description = "Allow printing the secrets to a terminal. They stay visible in its scrollback.",
    };

    public EnvCommand(CommandContext context)
    {
        _context = context;
        _shell.Validators.Add(static result =>
        {
            var text = result.GetValueOrDefault<string?>();
            if (text is not null && !ShellNames.TryParse(text, out _))
            {
                result.AddError($"--shell must be one of: {ShellNames.Choices} (got '{text}').");
            }
        });
    }

    public Command Build()
    {
        var command = new Command(
            "env",
            "Print a script that sets the variables in your shell session. Pipe it, do not read it: " +
            "envsync env --shell pwsh | Invoke-Expression   or   eval \"$(envsync env --shell bash)\".")
        {
            _shell,
            _unsafePrint,
        };

        command.SetAction(ExecuteAsync);
        return command;
    }

    private async Task<int> ExecuteAsync(ParseResult parse, CancellationToken cancellationToken)
    {
        if (!ShellNames.TryParse(parse.GetValue(_shell), out var shell))
        {
            shell = ShellNames.Default;
        }

        // Decided before a single secret is fetched: a refused request must not have touched any provider, and it prints nothing.
        if (!_context.Host.IsOutputRedirected && !parse.GetValue(_unsafePrint))
        {
            _context.StandardError.WriteLine(
                "error: refusing to print secrets to a terminal, where they would stay in the scrollback. " +
                "Pipe the output into your shell (`| Invoke-Expression`, `eval \"$(...)\"`), or pass --unsafe-print.");
            return ExitCodes.UsageError;
        }

        if (!_context.TryRead(parse, out var settings))
        {
            return await FailAsync(shell, ExitCodes.UsageError, cancellationToken);
        }

        var (profile, exitCode) = await _context.LoadProfileAsync(settings, cancellationToken);
        if (profile is null)
        {
            return await FailAsync(shell, exitCode, cancellationToken);
        }

        using var result = await _context.Services.Export.HandleAsync(new ExportEnvironmentQuery(profile, settings.Resolve, shell), cancellationToken);

        if (!result.Resolution.IsSatisfied)
        {
            ReportWriter.WriteFailure(_context.StandardError, profile.Name, result.Resolution, "no script was written");
            return await FailWithScriptAsync(result.FailureScript, ExitCodes.For(result.Resolution), cancellationToken);
        }

        if (!result.IsSuccess)
        {
            ErrorWriter.Write(_context.StandardError, result.EmitErrors);
            return await FailWithScriptAsync(result.FailureScript, ExitCodes.For(result.EmitErrors), cancellationToken);
        }

        ReportWriter.WriteWarnings(_context.StandardError, result.Resolution, inheritedValuesRemain: true);
        await ScriptWriter.WriteAsync(_context.Host.StandardOutput, result.Script, cancellationToken);
        return ExitCodes.Success;
    }

    private Task<int> FailAsync(ShellKind shell, int exitCode, CancellationToken cancellationToken) =>
        FailWithScriptAsync(_context.FailureScript(shell, exitCode), exitCode, cancellationToken);

    private async Task<int> FailWithScriptAsync(string script, int exitCode, CancellationToken cancellationToken)
    {
        if (script.Length > 0)
        {
            await ScriptWriter.WriteAsync(_context.Host.StandardOutput, script.AsMemory(), cancellationToken);
        }

        return exitCode;
    }
}
