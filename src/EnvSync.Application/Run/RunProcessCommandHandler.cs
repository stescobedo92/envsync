using System.Collections.Immutable;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Resolution;

namespace EnvSync.Application.Run;

/// <summary>
/// Preflight, then launch: if any required key is missing or unreadable the process is never started,
/// so the solution does not run half-configured.
/// </summary>
public sealed class RunProcessCommandHandler : ICommandHandler<RunProcessCommand, RunProcessResult>
{
    private readonly IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> _resolver;
    private readonly IProcessLauncher _launcher;

    public RunProcessCommandHandler(
        IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> resolver,
        IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(launcher);

        _resolver = resolver;
        _launcher = launcher;
    }

    public async ValueTask<RunProcessResult> HandleAsync(RunProcessCommand command, CancellationToken cancellationToken)
    {
        var resolution = await _resolver.HandleAsync(
            new ResolveEnvironmentQuery(command.Profile, command.Options, ResolveMode.Retain),
            cancellationToken);

        if (!resolution.IsSatisfied)
        {
            return new RunProcessResult(resolution, Launched: false, ExitCodes.For(resolution), LaunchError: null);
        }

        var launch = await _launcher.RunAsync(
            new ProcessLaunchRequest(command.Executable, command.Arguments, ToAssignments(resolution)),
            cancellationToken);

        return launch.IsSuccess
            ? new RunProcessResult(resolution, Launched: true, launch.Value, LaunchError: null)
            : new RunProcessResult(resolution, Launched: false, ExitCodes.For(launch.Errors[0].Kind), launch.Errors[0]);
    }

    private static ImmutableArray<EnvironmentAssignment> ToAssignments(ResolutionResult resolution)
    {
        var assignments = ImmutableArray.CreateBuilder<EnvironmentAssignment>(resolution.Outcomes.Length);

        foreach (var outcome in resolution.Outcomes)
        {
            if (outcome.Status == VariableStatus.Resolved)
            {
                assignments.Add(new EnvironmentAssignment(outcome.Spec.Name, outcome.Value));
            }
        }

        return assignments.ToImmutable();
    }
}
