using System.Collections.Immutable;
using EnvSync.Domain;

namespace EnvSync.Application.Abstractions;

/// <summary>One variable to overlay on the child's environment.</summary>
public readonly record struct EnvironmentAssignment(EnvironmentVariableName Name, SecretValue Value);

public readonly record struct ProcessLaunchRequest(
    string Executable,
    ImmutableArray<string> Arguments,
    ImmutableArray<EnvironmentAssignment> Environment);

/// <summary>Runs a child process with extra environment variables and waits for it.</summary>
public interface IProcessLauncher
{
    /// <returns>The child's exit code, or an <see cref="ErrorKind.ExecutableNotFound"/> / <see cref="ErrorKind.ProcessLaunchFailed"/> failure.</returns>
    ValueTask<Result<int>> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken);
}
