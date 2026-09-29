using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Providers;
using EnvSync.Domain;

namespace EnvSync.Application.Resolution;

/// <summary>
/// Resolves a profile's variables: one provider per referenced alias, a bounded number of concurrent requests,
/// a time budget per request, and every result written to its own slot of a preallocated array, so there are no
/// locks and the manifest order is preserved no matter which request finishes first.
/// </summary>
public sealed class ResolveEnvironmentQueryHandler : IQueryHandler<ResolveEnvironmentQuery, ResolutionResult>
{
    private readonly ProviderRegistry _registry;

    public ResolveEnvironmentQueryHandler(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public async ValueTask<ResolutionResult> HandleAsync(ResolveEnvironmentQuery query, CancellationToken cancellationToken)
    {
        var variables = query.Profile.Variables;
        if (variables.IsDefaultOrEmpty)
        {
            return new ResolutionResult([]);
        }

        var providers = CreateProviders(query.Profile);
        try
        {
            var outcomes = query.Mode == ResolveMode.Offline
                ? Unverified(variables, providers)
                : await FetchAsync(query, variables, providers, cancellationToken);

            return new ResolutionResult(ImmutableCollectionsMarshal.AsImmutableArray(outcomes));
        }
        finally
        {
            await DisposeAsync(providers);
        }
    }

    private Dictionary<string, Result<ISecretProvider>> CreateProviders(Profile profile)
    {
        var providers = new Dictionary<string, Result<ISecretProvider>>(StringComparer.Ordinal);

        foreach (var variable in profile.Variables)
        {
            if (providers.ContainsKey(variable.From))
            {
                continue;
            }

            providers[variable.From] = FindDefinition(profile, variable.From) is { } definition
                ? _registry.Create(definition)
                : Result<ISecretProvider>.Failure(new Error(
                    ErrorKind.ProviderUnknown,
                    variable.From,
                    $"Provider '{variable.From}' is not declared in profile '{profile.Name}'."));
        }

        return providers;
    }

    private static ProviderDefinition? FindDefinition(Profile profile, string alias)
    {
        foreach (var provider in profile.Providers)
        {
            if (string.Equals(provider.Alias, alias, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    private static VariableOutcome[] Unverified(
        ImmutableArray<VariableSpec> variables,
        Dictionary<string, Result<ISecretProvider>> providers)
    {
        var outcomes = new VariableOutcome[variables.Length];

        for (var i = 0; i < outcomes.Length; i++)
        {
            var spec = variables[i];
            var created = providers[spec.From];
            outcomes[i] = created.IsSuccess
                ? new VariableOutcome(spec, VariableStatus.Unverified, default, default)
                : ProviderUnavailable(spec, created.Errors[0]);
        }

        return outcomes;
    }

    private static async ValueTask<VariableOutcome[]> FetchAsync(
        ResolveEnvironmentQuery query,
        ImmutableArray<VariableSpec> variables,
        Dictionary<string, Result<ISecretProvider>> providers,
        CancellationToken cancellationToken)
    {
        var outcomes = new VariableOutcome[variables.Length];
        var retain = query.Mode == ResolveMode.Retain;
        var timeout = query.Options.Timeout;
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, query.Options.MaxConcurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, variables.Length),
            parallel,
            async (index, token) => outcomes[index] = await ResolveOneAsync(variables[index], providers[variables[index].From], timeout, retain, token));

        return outcomes;
    }

    private static async ValueTask<VariableOutcome> ResolveOneAsync(
        VariableSpec spec,
        Result<ISecretProvider> created,
        TimeSpan timeout,
        bool retain,
        CancellationToken cancellationToken)
    {
        if (!created.IsSuccess)
        {
            return ProviderUnavailable(spec, created.Errors[0]);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            budget.CancelAfter(timeout);
        }

        Result<SecretValue> fetched;
        try
        {
            fetched = await created.Value.GetAsync(spec.Reference, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(spec, ErrorKind.Timeout, $"Provider '{spec.From}' did not answer within {timeout.TotalSeconds:0.##}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(spec, ErrorKind.ProviderUnavailable, $"Provider '{spec.From}' failed: {ex.Message}");
        }

        return ToOutcome(spec, fetched, retain);
    }

    private static VariableOutcome ToOutcome(VariableSpec spec, Result<SecretValue> fetched, bool retain)
    {
        if (fetched.IsSuccess)
        {
            return new VariableOutcome(spec, VariableStatus.Resolved, retain ? fetched.Value : default, default);
        }

        var error = fetched.Errors[0] with { Subject = spec.Name.Value };
        var status = error.Kind is ErrorKind.SecretNotFound or ErrorKind.FieldNotFound
            ? (spec.Required ? VariableStatus.Missing : VariableStatus.Skipped)
            : VariableStatus.Failed;

        return new VariableOutcome(spec, status, default, error);
    }

    private static VariableOutcome ProviderUnavailable(VariableSpec spec, Error creationError) =>
        Failed(spec, creationError.Kind, $"Provider '{spec.From}': {creationError.Detail}");

    private static VariableOutcome Failed(VariableSpec spec, ErrorKind kind, string detail) =>
        new(spec, VariableStatus.Failed, default, new Error(kind, spec.Name.Value, detail));

    private static async ValueTask DisposeAsync(Dictionary<string, Result<ISecretProvider>> providers)
    {
        foreach (var created in providers.Values)
        {
            if (!created.IsSuccess)
            {
                continue;
            }

            switch (created.Value)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
