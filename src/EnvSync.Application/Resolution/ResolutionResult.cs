using System.Collections.Immutable;
using EnvSync.Domain;

namespace EnvSync.Application.Resolution;

/// <summary>The outcome of resolving every variable of a profile, in manifest order.</summary>
public sealed class ResolutionResult
{
    public ResolutionResult(ImmutableArray<VariableOutcome> outcomes)
    {
        Outcomes = outcomes.IsDefault ? [] : outcomes;
        Errors = CollectErrors(Outcomes);
    }

    public ImmutableArray<VariableOutcome> Outcomes { get; }

    /// <summary>The errors that block the run: required keys that are missing and variables that failed.</summary>
    public ImmutableArray<Error> Errors { get; }

    public bool IsSatisfied => Errors.IsEmpty;

    private static ImmutableArray<Error> CollectErrors(ImmutableArray<VariableOutcome> outcomes)
    {
        ImmutableArray<Error>.Builder? errors = null;

        foreach (var outcome in outcomes)
        {
            if (outcome.Status is VariableStatus.Missing or VariableStatus.Failed)
            {
                errors ??= ImmutableArray.CreateBuilder<Error>();
                errors.Add(outcome.Error);
            }
        }

        return errors?.ToImmutable() ?? [];
    }
}
