using System.Collections.Immutable;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Buffers;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Application.Export;

public sealed class ExportEnvironmentQueryHandler : IQueryHandler<ExportEnvironmentQuery, ExportResult>
{
    private readonly IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> _resolver;
    private readonly IShellEmitter[] _emitters;

    public ExportEnvironmentQueryHandler(
        IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> resolver,
        IEnumerable<IShellEmitter> emitters)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(emitters);

        _resolver = resolver;
        _emitters = [.. emitters];
    }

    public async ValueTask<ExportResult> HandleAsync(ExportEnvironmentQuery query, CancellationToken cancellationToken)
    {
        var emitter = FindEmitter(query.Shell);
        var resolution = await _resolver.HandleAsync(
            new ResolveEnvironmentQuery(query.Profile, query.Options, ResolveMode.Retain),
            cancellationToken);

        if (!resolution.IsSatisfied)
        {
            return new ExportResult(resolution, [], script: null);
        }

        var script = new PooledCharBufferWriter();
        var errors = ImmutableArray.CreateBuilder<Error>();

        foreach (var outcome in resolution.Outcomes)
        {
            if (outcome.Status == VariableStatus.Resolved
                && !emitter.TryWriteVariable(outcome.Spec.Name, outcome.Value.Reveal(), script))
            {
                errors.Add(new Error(
                    ErrorKind.UnsupportedValueForShell,
                    outcome.Spec.Name.Value,
                    $"The value cannot be written safely for {query.Shell}; use 'envsync run' instead."));
            }
        }

        if (errors.Count == 0)
        {
            return new ExportResult(resolution, [], script);
        }

        script.Dispose();
        return new ExportResult(resolution, errors.ToImmutable(), script: null);
    }

    private IShellEmitter FindEmitter(ShellKind shell)
    {
        foreach (var emitter in _emitters)
        {
            if (emitter.Shell == shell)
            {
                return emitter;
            }
        }

        throw new InvalidOperationException($"No shell emitter is registered for {shell}.");
    }
}
