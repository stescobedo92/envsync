using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using EnvSync.Application;
using EnvSync.Cli.Commands;

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

        await using var standardOutput = new StreamWriter(environment.StandardOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        var options = new GlobalOptions();
        var root = CommandTree.Build(new CommandContext(environment, services, options, standardOutput), options);

        try
        {
            var parse = root.Parse(args);

            if (parse.Action is ParseErrorAction)
            {
                foreach (var error in parse.Errors)
                {
                    environment.StandardError.WriteLine($"error: {error.Message}");
                }

                environment.StandardError.WriteLine("Try 'envsync --help' for usage.");
                return ExitCodes.UsageError;
            }

            var configuration = new InvocationConfiguration
            {
                Output = standardOutput,
                Error = environment.StandardError,
                EnableDefaultExceptionHandler = false,
            };

            return await parse.InvokeAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            environment.StandardError.WriteLine("envsync: cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            // Only the type and message: an exception is never a place where a secret should be.
            environment.StandardError.WriteLine($"envsync: unexpected error ({exception.GetType().Name}): {exception.Message}");
            return ExitCodes.InternalError;
        }
        finally
        {
            await standardOutput.FlushAsync(CancellationToken.None);
        }
    }
}
