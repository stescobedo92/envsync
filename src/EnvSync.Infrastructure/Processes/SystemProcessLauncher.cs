using System.ComponentModel;
using System.Diagnostics;
using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.Infrastructure.Processes;

/// <summary>
/// Starts the child with the secrets added to its own environment block only: nothing is written to disk and the
/// parent's environment is never modified. The child inherits stdin, stdout and stderr, so it keeps its terminal and its colors.
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so no shell ever re-parses them.
/// </summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(10);

    // ENOENT on Unix and ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND on Windows.
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;

    private readonly ExecutableResolver _resolver;
    private readonly TimeSpan _gracePeriod;

    public SystemProcessLauncher(ExecutableResolver resolver, TimeSpan? gracePeriod = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        _resolver = resolver;
        _gracePeriod = gracePeriod ?? DefaultGracePeriod;
    }

    public async ValueTask<Result<int>> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        if (_resolver.Resolve(request.Executable) is not { } executable)
        {
            return NotFound(request.Executable);
        }

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var assignment in request.Environment)
        {
            var value = assignment.Value.Reveal();
            if (value.Contains('\0', StringComparison.Ordinal))
            {
                // Refuse rather than truncate: a NUL would silently cut the secret in half.
                return Result<int>.Failure(new Error(
                    ErrorKind.ProcessLaunchFailed,
                    assignment.Name.Value,
                    "The value contains a NUL character, which an environment variable cannot hold."));
            }

            startInfo.Environment[assignment.Name.Value] = value;
        }

        if (!request.Unset.IsDefaultOrEmpty)
        {
            foreach (var name in request.Unset)
            {
                startInfo.Environment.Remove(name.Value);
            }
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is FileNotFound or PathNotFound)
        {
            return NotFound(request.Executable);
        }
        catch (Win32Exception exception)
        {
            return Result<int>.Failure(new Error(ErrorKind.ProcessLaunchFailed, request.Executable, exception.Message));
        }

        if (process is null)
        {
            return Result<int>.Failure(new Error(ErrorKind.ProcessLaunchFailed, request.Executable, "The operating system did not start the process."));
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process);
            }

            return Result<int>.Success(process.ExitCode);
        }
    }

    /// <summary>
    /// Ctrl+C reaches the child too, so it is given a grace period to shut down by itself before being stopped.
    /// </summary>
    private async Task StopAsync(Process process)
    {
        using var grace = new CancellationTokenSource(_gracePeriod);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill: nothing left to stop.
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static Result<int> NotFound(string command) =>
        Result<int>.Failure(new Error(ErrorKind.ExecutableNotFound, command, $"'{command}' was not found on PATH."));
}
