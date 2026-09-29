using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Sockets;
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
    // CancellationTokenSource.CancelAfter throws above this; a per-secret budget of 24 days is a mistake, not a setting.
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

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

        // Created before the try so that whatever was built is disposed even when a later factory misbehaves.
        var providers = new Dictionary<string, Result<ISecretProvider>>(StringComparer.Ordinal);
        try
        {
            CreateProviders(query.Profile, providers);

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

    private void CreateProviders(Profile profile, Dictionary<string, Result<ISecretProvider>> providers)
    {
        foreach (var variable in profile.Variables)
        {
            if (!providers.ContainsKey(variable.From))
            {
                providers[variable.From] = CreateProvider(profile, variable.From);
            }
        }
    }

    private Result<ISecretProvider> CreateProvider(Profile profile, string alias)
    {
        if (FindDefinition(profile, alias) is not { } definition)
        {
            return Result<ISecretProvider>.Failure(new Error(
                ErrorKind.ProviderUnknown,
                alias,
                $"Provider '{alias}' is not declared in profile '{profile.Name}'."));
        }

        try
        {
            return _registry.Create(definition);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The factory contract is to return a failure; one that throws is a bug, and must not take the other providers down.
            return Result<ISecretProvider>.Failure(new Error(ErrorKind.Internal, alias, $"{exception.GetType().Name}: {exception.Message}"));
        }
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
                : CreationFailed(spec, created.Errors);
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
            return CreationFailed(spec, created.Errors);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveTimeout = timeout > MaxTimeout ? MaxTimeout : timeout;
        if (effectiveTimeout > TimeSpan.Zero)
        {
            budget.CancelAfter(effectiveTimeout);
        }

        Result<SecretValue> fetched;
        try
        {
            fetched = await created.Value.GetAsync(spec.Reference, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only our own budget running out is a timeout; a provider that cancels itself is a different, unexplained problem.
            return budget.IsCancellationRequested
                ? Failed(spec, ErrorKind.Timeout, $"Provider '{spec.From}' did not answer within {effectiveTimeout.TotalSeconds:0.##}s.")
                : Failed(spec, ErrorKind.ProviderUnavailable, $"Provider '{spec.From}' cancelled the request on its own.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(spec, Classify(exception), $"Provider '{spec.From}' failed: {exception.GetType().Name}: {exception.Message}");
        }

        return ToOutcome(spec, fetched, retain);
    }

    /// <summary>An I/O style failure is an outage worth retrying; anything else is a bug and is reported as one.</summary>
    private static ErrorKind Classify(Exception exception) =>
        exception is IOException or HttpRequestException or SocketException or TimeoutException
            ? ErrorKind.ProviderUnavailable
            : ErrorKind.Internal;

    private static VariableOutcome ToOutcome(VariableSpec spec, Result<SecretValue> fetched, bool retain)
    {
        if (fetched.IsSuccess)
        {
            // An empty secret is not a value: a required variable must not "resolve" to nothing (on Windows the child
            // would not even see it), and this is checked before the value is dropped in Verify mode.
            return fetched.Value.Reveal().Length == 0
                ? Absent(spec, new Error(ErrorKind.SecretEmpty, spec.Name.Value, "The secret exists but its value is empty."))
                : new VariableOutcome(spec, VariableStatus.Resolved, retain ? fetched.Value : default, default);
        }

        var error = fetched.Errors[0] with { Subject = spec.Name.Value };
        return IsAbsence(error.Kind)
            ? Absent(spec, error)
            : new VariableOutcome(spec, VariableStatus.Failed, default, error);
    }

    /// <summary>
    /// Only "there is nothing there" may be tolerated for an optional variable. A wrong shape, a rejected credential or a timeout
    /// says something is broken, and hiding it would let a program run half-configured.
    /// </summary>
    private static bool IsAbsence(ErrorKind kind) =>
        kind is ErrorKind.SecretNotFound or ErrorKind.FieldNotFound or ErrorKind.SecretEmpty;

    private static VariableOutcome Absent(VariableSpec spec, Error error) =>
        new(spec, spec.Required ? VariableStatus.Missing : VariableStatus.Skipped, default, error);

    private static VariableOutcome CreationFailed(VariableSpec spec, ImmutableArray<Error> errors)
    {
        // Every problem, so the user fixes them all in one pass instead of one per run.
        var detail = string.Join(" ", errors.Select(static e => e.Detail).Where(static d => d.Length > 0).Distinct(StringComparer.Ordinal));
        return Failed(spec, errors[0].Kind, $"Provider '{spec.From}': {detail}");
    }

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

            try
            {
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
#pragma warning disable CA1031 // A provider that fails to clean up must neither mask the result nor stop the others from being cleaned up.
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }
    }
}
