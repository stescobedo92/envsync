using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using EnvSync.Application;
using EnvSync.Cli.Commands;
using EnvSync.Cli.Output;

namespace EnvSync.Cli;

internal static class CliApplication
{
    /// <returns>The process exit code. See <see cref="ExitCodes"/>.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        CliEnvironment environment,
        CliServices? services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        services ??= CompositionRoot.Build(environment);

        // Deliberately not disposed: with leaveOpen there is nothing to release, and disposing would flush a second time, outside the
        // protection below, so a closed pipe would turn a real exit code into an unhandled exception.
#pragma warning disable CA2000
        var standardOutput = new StreamWriter(environment.StandardOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
#pragma warning restore CA2000
        var options = new GlobalOptions();
        var context = new CommandContext(environment, services, options, standardOutput, hasDoubleDash: Array.IndexOf(args, "--") >= 0);
        var root = CommandTree.Build(context, options);

        try
        {
            // No "@file" response files: an argument that merely starts with @ must never be expanded into more options and words.
            var parse = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });

            if (parse.Action is ParseErrorAction)
            {
                foreach (var error in parse.Errors)
                {
                    environment.StandardError.WriteLine($"error: {ErrorWriter.Safe(error.Message)}");
                }

                environment.StandardError.WriteLine("Try 'envsync --help' for usage.");
                await FailEnvConsumerAsync(parse, args, context, environment, cancellationToken);
                return ExitCodes.UsageError;
            }

            var configuration = new InvocationConfiguration
            {
                Output = standardOutput,
                Error = environment.StandardError,
                EnableDefaultExceptionHandler = false,

                // System.CommandLine would otherwise install its own SIGINT/SIGTERM handlers, give the action only two seconds and
                // then abandon it: a running child would be orphaned and Ctrl+C during resolving would not reach Program.cs's token.
                ProcessTerminationTimeout = null,
            };

            return await parse.InvokeAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Timeouts are turned into typed errors inside the handlers, so a cancellation that reaches here is a stop request.
            environment.StandardError.WriteLine("envsync: cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            // Only the type and message: an exception is never a place where a secret should be.
            environment.StandardError.WriteLine($"envsync: unexpected error ({exception.GetType().Name}): {ErrorWriter.Safe(exception.Message)}");
            return ExitCodes.InternalError;
        }
        finally
        {
            await FlushQuietlyAsync(standardOutput);
        }
    }

    /// <summary>
    /// A typo such as <c>envsync env --shell bash --nonsense</c> would otherwise print nothing, and <c>eval "$(...)"</c> of nothing
    /// succeeds. The statement that fails the consumer is written to stdout for <c>env</c> too.
    /// </summary>
    private static async Task FailEnvConsumerAsync(
        ParseResult parse,
        string[] args,
        CommandContext context,
        CliEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (parse.CommandResult.Command.Name != "env" || !environment.IsOutputRedirected)
        {
            return;
        }

        // A shell we do not support has a dialect we do not know: printing another one's syntax could do harm, so nothing is printed.
        if (!ShellNames.TryFromArguments(args, out var shell))
        {
            return;
        }

        var script = context.FailureScript(shell, ExitCodes.UsageError);
        if (script.Length > 0)
        {
            await ScriptWriter.WriteAsync(environment.StandardOutput, script.AsMemory(), cancellationToken);
        }
    }

    /// <summary>A closed pipe (<c>envsync check | head -0</c>) must not turn a real exit code into an unhandled exception.</summary>
    private static async Task FlushQuietlyAsync(StreamWriter writer)
    {
        try
        {
            await writer.FlushAsync(CancellationToken.None);
        }
        catch (IOException)
        {
            // Nobody is reading stdout any more; there is nothing left to tell them.
        }
    }
}
