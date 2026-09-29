using EnvSync.Application.Abstractions;
using EnvSync.Application.Resolution;
using EnvSync.Domain;

namespace EnvSync.Application.Requirements;

/// <summary>Ask whether every key a profile needs is available, without ever keeping a secret value.</summary>
/// <param name="Offline">Only validate the providers' configuration; make no network request.</param>
public readonly record struct CheckRequirementsQuery(Profile Profile, ResolveOptions Options, bool Offline);

public sealed class CheckRequirementsQueryHandler : IQueryHandler<CheckRequirementsQuery, ResolutionResult>
{
    private readonly IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> _resolver;

    public CheckRequirementsQueryHandler(IQueryHandler<ResolveEnvironmentQuery, ResolutionResult> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public ValueTask<ResolutionResult> HandleAsync(CheckRequirementsQuery query, CancellationToken cancellationToken) =>
        _resolver.HandleAsync(
            new ResolveEnvironmentQuery(query.Profile, query.Options, query.Offline ? ResolveMode.Offline : ResolveMode.Verify),
            cancellationToken);
}
